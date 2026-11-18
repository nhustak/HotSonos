using System.Globalization;
using System.Text.Json;

namespace HotSonos.Activity;

public sealed class ActivityLogOptions
{
    public string? DatabasePath { get; init; }
    public string? PlayHistoryPath { get; init; }
    public string? PlayEventsPath { get; init; }
    public int RetentionDays { get; init; } = 180;
    public int MaxEvents { get; init; } = 80_000;
    public int MaxSessions { get; init; } = 40_000;
    public bool Seed { get; init; } = true;
}

/// <summary>
/// Facade over <see cref="PlaySessionTracker"/> and <see cref="ActivityStore"/>.
/// Failures inside the store are the caller's to catch — SonosManager swallows them
/// so a report-log problem cannot stop playback. This type never writes play-history.json.
/// </summary>
public sealed class ActivityLog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly PlaySessionTracker _tracker = new();
    private readonly ActivityStore? _store;
    private readonly bool _enabled;
    private readonly TrackerStep _retained = new();
    private DateTime _lastVolumeEventUtc = DateTime.MinValue;
    private DateTime _lastMuteEventUtc = DateTime.MinValue;

    public ActivityLog(ActivityLogOptions? options = null)
    {
        options ??= new ActivityLogOptions();
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos");
        var dbPath = options.DatabasePath ?? Path.Combine(dir, "activity.db");
        DatabasePath = dbPath;
        _store = new ActivityStore(dbPath, options.RetentionDays, options.MaxEvents, options.MaxSessions);
        // Reports record whenever the app runs. Shuffle already keeps last-played
        // for exclusion; this database is that history for the Reports tab.
        // Default-off would leave the page empty.
        _enabled = true;
        if (options.Seed)
            SeedOnce(options, dir);
    }

    private ActivityLog(bool enabled)
    {
        _enabled = enabled;
        DatabasePath = "";
    }

    /// <summary>No-op log used when the database cannot be opened. Reports stay empty.</summary>
    public static ActivityLog Empty() => new(false);

    public string DatabasePath { get; }

    public bool IsEnabled => _enabled;

    public void Apply(PlaySnapshot snapshot)
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
        {
            var step = _tracker.Apply(snapshot);
            PersistStep(step);
        }
    }

    public void NoteHotkeyNext() =>
        NoteArmed(PlayTriggers.HotkeyNext, TimeSpan.FromSeconds(20), "hotkey_next", "Hotkey: next");

    public void NoteHotkeyPrevious() =>
        NoteArmed(PlayTriggers.HotkeyPrevious, TimeSpan.FromSeconds(20), "hotkey_previous", "Hotkey: previous");

    public void NoteShuffleStart(string? detail = null)
    {
        var summary = string.IsNullOrWhiteSpace(detail) ? "Shuffle library started" : detail.Trim();
        NoteArmed(PlayTriggers.ShuffleStart, TimeSpan.FromMinutes(10), "shuffle_start", summary);
    }

    public void NotePlayNow(string? label = null)
    {
        var summary = string.IsNullOrWhiteSpace(label) ? "Play now" : "Play now: " + label.Trim();
        NoteArmed(PlayTriggers.PlayNow, TimeSpan.FromMinutes(2), "play_now", summary);
    }

    public void NoteHotkey(string kind, string summary)
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
            PersistStep(Single(SonosEvent(DateTime.UtcNow, kind, summary, zone: _tracker.OpenZone)));
    }

    public void NoteVolume(string summary)
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
        {
            _lastVolumeEventUtc = DateTime.UtcNow;
            PersistStep(Single(SonosEvent(_lastVolumeEventUtc, "volume", summary, _tracker.OpenZone)));
        }
    }

    public void NoteMute(bool muted)
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
        {
            _lastMuteEventUtc = DateTime.UtcNow;
            PersistStep(Single(SonosEvent(_lastMuteEventUtc, "mute", muted ? "Muted" : "Unmuted", _tracker.OpenZone)));
        }
    }

    /// <summary>GENA volume with no level. Skipped when we just wrote the change ourselves.</summary>
    public void NoteExternalVolume()
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastVolumeEventUtc).TotalSeconds < 3)
                return;
            _lastVolumeEventUtc = now;
            PersistStep(Single(SonosEvent(now, "volume", "Volume changed (speaker or another controller)", _tracker.OpenZone)));
        }
    }

    /// <summary>GENA mute. Skipped when we just wrote the mute ourselves.</summary>
    public void NoteExternalMute(bool? muted)
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastMuteEventUtc).TotalSeconds < 3)
                return;
            _lastMuteEventUtc = now;
            var summary = muted switch
            {
                true => "Muted (speaker or another controller)",
                false => "Unmuted (speaker or another controller)",
                _ => "Mute changed (speaker or another controller)",
            };
            PersistStep(Single(SonosEvent(now, "mute", summary, _tracker.OpenZone)));
        }
    }

    public void NoteZonePresence(string room, bool online, bool activeCoordinator)
    {
        if (!_enabled || _store is null || string.IsNullOrWhiteSpace(room))
            return;
        lock (_gate)
        {
            if (!online && activeCoordinator)
            {
                var step = _tracker.ForceClose(DateTime.UtcNow, PlayTriggers.SpeakerLost);
                PersistStep(step);
            }

            var kind = online ? "zone_online" : "zone_offline";
            var summary = online ? $"{room} is back online" : $"{room} went offline";
            PersistStep(Single(SonosEvent(DateTime.UtcNow, kind, summary, room)));
        }
    }

    public void NoteReachability(string room, string? ip, bool online, bool isCoordinator, string? detail)
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
        {
            if (!online && isCoordinator)
            {
                var step = _tracker.ForceClose(DateTime.UtcNow, PlayTriggers.SpeakerLost);
                PersistStep(step);
            }

            var kind = online ? "speaker_recovered" : "speaker_unreachable";
            var where = string.IsNullOrWhiteSpace(ip) ? room : $"{room} ({ip})";
            var summary = string.IsNullOrWhiteSpace(detail)
                ? (online ? $"{where} is answering again" : $"{where} stopped answering")
                : detail.Trim();
            PersistStep(Single(SonosEvent(DateTime.UtcNow, kind, summary, room)));
        }
    }

    public void NoteGroupChanged(string summary)
    {
        if (!_enabled || _store is null || string.IsNullOrWhiteSpace(summary))
            return;
        lock (_gate)
        {
            PersistStep(Single(SonosEvent(DateTime.UtcNow, "group_changed", summary.Trim(), _tracker.OpenZone)));
        }
    }

    public void NoteDiscovery(bool ok, string summary)
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
        {
            PersistStep(Single(SonosEvent(
                DateTime.UtcNow,
                ok ? "discovery_recovered" : "discovery_failed",
                summary,
                _tracker.OpenZone)));
        }
    }

    public void NoteConnection(bool ok, string summary)
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
        {
            PersistStep(Single(SonosEvent(
                DateTime.UtcNow,
                ok ? "connection_recovered" : "connection_failed",
                summary,
                _tracker.OpenZone)));
        }
    }

    public void NoteError(string summary, string? detail = null)
    {
        if (!_enabled || _store is null || string.IsNullOrWhiteSpace(summary))
            return;
        lock (_gate)
        {
            PersistStep(Single(new ActivityEventDraft
            {
                Utc = DateTime.UtcNow,
                Category = ActivityCategories.Sonos,
                Kind = "error",
                Summary = summary.Trim(),
                Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
                Zone = _tracker.OpenZone,
            }));
        }
    }

    public IReadOnlyList<SongStatRow> SearchSongs(string? query, int limit = 200)
    {
        if (!_enabled || _store is null)
            return [];
        lock (_gate)
            return _store.SearchSongs(query, limit);
    }

    public IReadOnlyList<PlaySessionRow> PlaysForSong(string trackKey, int limit = 500, int offset = 0)
    {
        if (!_enabled || _store is null || string.IsNullOrWhiteSpace(trackKey))
            return [];
        lock (_gate)
            return _store.PlaysForSong(trackKey, limit, offset);
    }

    public MusicReportData QueryMusic(
        DateTime? fromLocal, DateTime? toLocalInclusive, bool skippedOnly, int limit = 500, int offset = 0)
    {
        if (!_enabled || _store is null)
            return new MusicReportData();
        var fromUtc = fromLocal is DateTime f ? LocalDateStartUtc(f) : (DateTime?)null;
        var toUtc = toLocalInclusive is DateTime t ? LocalDateStartUtc(t.Date.AddDays(1)) : (DateTime?)null;
        lock (_gate)
            return _store.QueryMusic(fromUtc, toUtc, skippedOnly, limit, offset);
    }

    public IReadOnlyList<ActivityEventRow> QueryEvents(string? category, string? kind, int limit = 500, int offset = 0)
    {
        if (!_enabled || _store is null)
            return [];
        lock (_gate)
            return _store.QueryEvents(category, kind, limit, offset);
    }

    public IReadOnlyList<string> EventKinds(string? category)
    {
        if (!_enabled || _store is null)
            return [];
        lock (_gate)
            return _store.EventKinds(category);
    }

    /// <summary>Forget a trigger armed for a Sonos command that failed.</summary>
    public void Disarm(string trigger)
    {
        if (!_enabled || _store is null || string.IsNullOrWhiteSpace(trigger))
            return;
        lock (_gate)
            _tracker.Disarm(trigger);
    }

    public void Dispose()
    {
        if (_store is null)
            return;
        lock (_gate)
        {
            try
            {
                PersistStep(_tracker.Checkpoint(DateTime.UtcNow));
            }
            catch
            {
                /* exit cannot retry a failed write */
            }

            _store.Dispose();
        }
    }

    private void NoteArmed(string trigger, TimeSpan ttl, string kind, string summary)
    {
        if (!_enabled || _store is null)
            return;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _tracker.Arm(trigger, now, ttl);
            var label = ActivityText.TrackLabel(_tracker.OpenTitle, _tracker.OpenArtist, _tracker.OpenTrackKey);
            var text = _tracker.HasOpenSession ? $"{summary} · {label}" : summary;
            PersistStep(Single(SonosEvent(now, kind, text, _tracker.OpenZone)));
        }
    }

    /// <summary>
    /// Writes <paramref name="step"/> and anything a previous write failed to store.
    /// On failure the batch stays in memory and the next call tries again, so a
    /// closed session is not dropped just because SQLite threw once.
    /// </summary>
    private void PersistStep(TrackerStep step)
    {
        if (_store is null)
            return;
        if (_retained.Closed.Count > 0 || _retained.Events.Count > 0)
        {
            var merged = new TrackerStep();
            merged.Closed.AddRange(_retained.Closed);
            merged.Events.AddRange(_retained.Events);
            merged.Closed.AddRange(step.Closed);
            merged.Events.AddRange(step.Events);
            _retained.Closed.Clear();
            _retained.Events.Clear();
            step = merged;
        }
        else if (step.Closed.Count == 0 && step.Events.Count == 0)
        {
            return;
        }

        const int maxSessions = 50;
        const int maxEvents = 200;
        if (step.Closed.Count > maxSessions)
            step.Closed.RemoveRange(0, step.Closed.Count - maxSessions);
        if (step.Events.Count > maxEvents)
            step.Events.RemoveRange(0, step.Events.Count - maxEvents);

        try
        {
            _store.Persist(step);
        }
        catch
        {
            _retained.Closed.AddRange(step.Closed);
            _retained.Events.AddRange(step.Events);
            throw;
        }
    }

    private static TrackerStep Single(ActivityEventDraft draft)
    {
        var step = new TrackerStep();
        step.Events.Add(draft);
        return step;
    }

    private void SeedOnce(ActivityLogOptions options, string dir)
    {
        if (_store is null || _store.HasMeta("seed_v1"))
            return;
        var historyPath = options.PlayHistoryPath ?? Path.Combine(dir, "play-history.json");
        var eventsPath = options.PlayEventsPath ?? Path.Combine(dir, "play-events.jsonl");
        var sessions = ActivitySeed.Build(ReadHistory(historyPath), ReadPlayEvents(eventsPath));
        var stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        _store.InsertSeedSessions(sessions, "seed_v1", stamp);
    }

    internal static List<SeedHistoryEntry> ReadHistory(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            var doc = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(path), JsonOptions);
            return doc?.Entries ?? [];
        }
        catch
        {
            return [];
        }
    }

    internal static List<SeedPlayEvent> ReadPlayEvents(string path)
    {
        var list = new List<SeedPlayEvent>();
        try
        {
            if (!File.Exists(path))
                return list;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<SeedPlayEvent>(line, JsonOptions);
                    if (evt is not null)
                        list.Add(evt);
                }
                catch
                {
                    /* skip a bad line */
                }
            }
        }
        catch
        {
            /* unreadable trail — live recording still works */
        }

        return list;
    }

    private static ActivityEventDraft SonosEvent(DateTime utc, string kind, string summary, string? zone) => new()
    {
        Utc = utc,
        Category = ActivityCategories.Sonos,
        Kind = kind,
        Summary = summary,
        Zone = zone,
    };

    private static DateTime LocalDateStartUtc(DateTime localDate)
    {
        var unspecified = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        var local = DateTime.SpecifyKind(unspecified, DateTimeKind.Local);
        return local.ToUniversalTime();
    }

    private sealed class HistoryFile
    {
        public List<SeedHistoryEntry>? Entries { get; set; }
    }
}
