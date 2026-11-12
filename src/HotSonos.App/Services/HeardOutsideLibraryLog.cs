using System.IO;
using System.Text.Json;
using HotSonos.App.Infrastructure;
using HotSonos.Core.Models;

namespace HotSonos.App.Services;

/// <summary>
/// Names of tracks that played from Spotify, radio, and other non-library sources.
/// One row per track (last heard time updates when it plays again). Library SMB
/// paths stay in <see cref="PlayHistoryStore"/> for shuffle exclusion and are not listed here.
/// File: %LocalAppData%\HotSonos\heard-outside-library.json
/// </summary>
public sealed class HeardOutsideLibraryLog
{
    public const int MaxEntries = 500;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan SamePlayWindow = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions PlayEventJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly string _playEventsPath;
    private readonly object _gate = new();
    private readonly List<HeardTrack> _entries = [];

    public HeardOutsideLibraryLog(string? path = null, string? playEventsPath = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "heard-outside-library.json");
        _playEventsPath = playEventsPath ?? Path.Combine(dir, "play-events.jsonl");

        var existed = File.Exists(_path);
        Load();
        if (existed)
            return;

        var imported = ImportPlayEvents();
        SaveUnlocked();
        AppLog.Info($"Heard outside library: imported {imported} track(s) from play events");
    }

    public string FilePath => _path;

    /// <summary>Raised after the list changes. Subscribers must not run Sonos commands.</summary>
    public event Action? Changed;

    public void RecordPlay(NowPlaying np) => Apply(np, bumpPlay: true);

    /// <summary>Fill artist/album that arrived after the track started. Does not add another play.</summary>
    public void FillIn(NowPlaying np) => Apply(np, bumpPlay: false);

    public bool Remove(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        bool removed;
        lock (_gate)
        {
            removed = _entries.RemoveAll(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                SaveUnlocked();
        }

        if (removed)
            RaiseChanged();
        return removed;
    }

    /// <summary>Newest last-heard first.</summary>
    public IReadOnlyList<HeardTrack> GetRecent(int max = 200)
    {
        max = Math.Clamp(max, 1, MaxEntries);
        lock (_gate)
        {
            return _entries
                .OrderByDescending(e => e.LastUtc)
                .Take(max)
                .Select(Clone)
                .ToList();
        }
    }

    private void Apply(NowPlaying np, bool bumpPlay)
    {
        if (!NowPlaying.IsHeardOutsideLibrary(np.TrackUri, np.Title, np.IsEmpty))
            return;

        var utc = DateTime.UtcNow;
        var key = Identity(np.TrackUri, np.Title, np.Artist);
        if (key.Length == 0)
            return;

        var title = np.Title?.Trim() ?? "";
        if (title.Length == 0)
            return;
        var artist = BlankToNull(np.Artist);
        var album = BlankToNull(np.Album);
        var source = BlankToNull(np.SourceLabel);
        var uri = BlankToNull(np.TrackUri);
        var changed = false;
        string? logged = null;

        lock (_gate)
        {
            var existing = Find(key);
            if (existing is null)
            {
                _entries.Add(new HeardTrack
                {
                    Key = key,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    Source = source,
                    Uri = uri,
                    FirstUtc = utc,
                    LastUtc = utc,
                    Plays = 1,
                });
                changed = true;
                logged = Display(title, artist);
            }
            else if ((utc - existing.LastUtc) < SamePlayWindow)
            {
                changed = Merge(existing, title, artist, album, source, uri);
            }
            else if (bumpPlay)
            {
                existing.Plays++;
                existing.LastUtc = utc;
                Merge(existing, title, artist, album, source, uri);
                changed = true;
                logged = Display(existing.Title, existing.Artist);
            }
            else
            {
                changed = Merge(existing, title, artist, album, source, uri);
            }

            if (!changed)
                return;

            PruneUnlocked();
            SaveUnlocked();
        }

        if (logged is not null)
        {
            AppLog.Info($"Heard outside library: {logged}" +
                        (string.IsNullOrEmpty(source) ? "" : $" | {source}"));
        }

        RaiseChanged();
    }

    private HeardTrack? Find(string key) =>
        _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

    private static bool Merge(
        HeardTrack entry,
        string title,
        string? artist,
        string? album,
        string? source,
        string? uri)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(title) && !string.Equals(entry.Title, title, StringComparison.Ordinal))
        {
            entry.Title = title;
            changed = true;
        }

        changed |= AssignIfBlank(entry.Artist, artist, v => entry.Artist = v);
        changed |= AssignIfBlank(entry.Album, album, v => entry.Album = v);
        changed |= AssignIfBlank(entry.Source, source, v => entry.Source = v);
        changed |= AssignIfBlank(entry.Uri, uri, v => entry.Uri = v);
        return changed;
    }

    private static bool AssignIfBlank(string? current, string? incoming, Action<string> set)
    {
        if (!string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(incoming))
            return false;
        set(incoming.Trim());
        return true;
    }

    private int ImportPlayEvents()
    {
        if (!File.Exists(_playEventsPath))
            return 0;

        var added = 0;
        try
        {
            foreach (var line in File.ReadLines(_playEventsPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                PlayEventLine? evt;
                try
                {
                    evt = JsonSerializer.Deserialize<PlayEventLine>(line, PlayEventJson);
                }
                catch
                {
                    continue;
                }

                if (evt is null
                    || !string.Equals(evt.Kind, "started", StringComparison.OrdinalIgnoreCase)
                    || !NowPlaying.IsHeardOutsideLibrary(evt.Uri, evt.Title))
                    continue;

                var utc = evt.Utc;
                if (utc == default)
                    utc = DateTime.UtcNow;
                else if (utc.Kind == DateTimeKind.Local)
                    utc = utc.ToUniversalTime();
                else if (utc.Kind == DateTimeKind.Unspecified)
                    utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                if (DateTime.UtcNow - utc > Retention)
                    continue;

                var key = Identity(evt.Uri, evt.Title, evt.Artist);
                var title = evt.Title?.Trim() ?? "";
                if (key.Length == 0 || title.Length == 0)
                    continue;
                var existing = Find(key);
                if (existing is null)
                {
                    _entries.Add(new HeardTrack
                    {
                        Key = key,
                        Title = title,
                        Artist = BlankToNull(evt.Artist),
                        Source = BlankToNull(NowPlaying.FormatSourceLabel(evt.Uri)),
                        Uri = BlankToNull(evt.Uri),
                        FirstUtc = utc,
                        LastUtc = utc,
                        Plays = 1,
                    });
                    added++;
                }
                else if (utc >= existing.LastUtc && (utc - existing.LastUtc) >= SamePlayWindow)
                {
                    existing.Plays++;
                    existing.LastUtc = utc;
                    Merge(existing, title, BlankToNull(evt.Artist), null, existing.Source, BlankToNull(evt.Uri));
                }
                else if (utc < existing.FirstUtc)
                {
                    existing.FirstUtc = utc;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Heard outside library import failed", ex);
        }

        PruneUnlocked();
        return _entries.Count > 0 ? _entries.Count : added;
    }

    private void PruneUnlocked()
    {
        var cutoff = DateTime.UtcNow - Retention;
        _entries.RemoveAll(e => e.LastUtc < cutoff);
        if (_entries.Count <= MaxEntries)
            return;

        var keep = _entries
            .OrderByDescending(e => e.LastUtc)
            .Take(MaxEntries)
            .ToList();
        _entries.Clear();
        _entries.AddRange(keep);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var doc = JsonSerializer.Deserialize<HeardDoc>(File.ReadAllText(_path), JsonOptions);
            if (doc?.Entries is null)
                return;
            _entries.AddRange(doc.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.Title)));
            PruneUnlocked();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Heard outside library load failed", ex);
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            var doc = new HeardDoc { Entries = _entries.OrderBy(e => e.LastUtc).ToList() };
            File.WriteAllText(_path, JsonSerializer.Serialize(doc, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Heard outside library save failed", ex);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Heard list subscriber failed", ex);
        }
    }

    private static string Identity(string? uri, string? title, string? artist)
    {
        var kind = NowPlaying.ClassifySource(uri);
        var uriKey = PlayHistoryStore.NormalizeKey(uri);
        if (NowPlaying.UsesPerTrackUri(kind) && uriKey.Length > 0)
            return "u:" + uriKey;

        var name = (title ?? "").Trim().ToLowerInvariant();
        var who = (artist ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0 && who.Length == 0)
            return "";
        if (uriKey.Length > 0)
            return "n:" + uriKey + "\n" + name + "\n" + who;
        return "n:" + kind + "\n" + name + "\n" + who;
    }

    private static HeardTrack Clone(HeardTrack e) => new()
    {
        Key = e.Key,
        Title = e.Title,
        Artist = e.Artist,
        Album = e.Album,
        Source = e.Source,
        Uri = e.Uri,
        FirstUtc = e.FirstUtc,
        LastUtc = e.LastUtc,
        Plays = e.Plays,
    };

    private static string Display(string? title, string? artist) =>
        string.IsNullOrWhiteSpace(artist) ? (title ?? "").Trim() : $"{title?.Trim()} — {artist.Trim()}";

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class HeardDoc
    {
        public List<HeardTrack> Entries { get; set; } = [];
    }

    private sealed class PlayEventLine
    {
        public DateTime Utc { get; set; }
        public string? Kind { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Uri { get; set; }
    }
}

/// <summary>One non-library track the house has played.</summary>
public sealed class HeardTrack
{
    public string Key { get; set; } = "";
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Source { get; set; }
    public string? Uri { get; set; }
    public DateTime FirstUtc { get; set; }
    public DateTime LastUtc { get; set; }
    public int Plays { get; set; }
}
