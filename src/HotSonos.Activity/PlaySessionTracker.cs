namespace HotSonos.Activity;

/// <summary>
/// Turns now-playing observations into closed play sessions and music/transport events.
/// Shuffle's last-played store is not touched here. An explicit Next/Previous/shuffle
/// applies only to the session that was already open when the command was armed, so
/// the song that starts afterwards is not labeled with the previous command.
/// </summary>
public sealed class PlaySessionTracker
{
    /// <summary>Playing samples further apart than this are not added (sleep, stalled poll).</summary>
    public const double MaxSampleGapSeconds = 120;

    /// <summary>A single start-to-end span with no middle sample, capped so a clock jump cannot become a play.</summary>
    public const double MaxSparseSpanSeconds = 6 * 60 * 60;

    private OpenSession? _open;
    private int _nextId = 1;
    private string? _pendingTrigger;
    private DateTime _pendingUntil;
    private int _pendingSessionId;

    public bool HasOpenSession => _open is not null;
    public string? OpenTrackKey => _open?.TrackKey;
    public string? OpenTitle => _open?.Title;
    public string? OpenArtist => _open?.Artist;
    public string? OpenZone => _open?.Zone;

    /// <summary>Remember a command until the open session closes, or until <paramref name="ttl"/> elapses.</summary>
    public void Arm(string trigger, DateTime utc, TimeSpan ttl)
    {
        _pendingTrigger = trigger;
        _pendingUntil = utc + ttl;
        _pendingSessionId = _open?.Id ?? 0;
    }

    public TrackerStep Apply(PlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ExpirePending(snapshot.Utc);
        var step = new TrackerStep();

        if (!HasIdentity(snapshot))
        {
            if (snapshot.IsStopped && _open is not null)
            {
                Credit(_open, snapshot.Utc, stillPlaying: false);
                Close(snapshot.Utc, PlayEndReasons.TransportStopped, step);
            }
            return step;
        }

        if (_open is null)
        {
            if (snapshot.IsPlaying || snapshot.IsPaused)
                Open(snapshot, step);
            return step;
        }

        if (string.Equals(_open.TrackKey, snapshot.TrackKey, StringComparison.OrdinalIgnoreCase))
        {
            if (IsRewind(_open, snapshot))
            {
                Credit(_open, snapshot.Utc, stillPlaying: false);
                Close(snapshot.Utc, PlayEndReasons.TrackChanged, step);
                if (snapshot.IsPlaying || snapshot.IsPaused)
                    Open(snapshot, step);
                return step;
            }

            var wasPlaying = _open.LastWasPlaying;
            var wasPaused = _open.LastWasPaused;
            Credit(_open, snapshot.Utc, stillPlaying: snapshot.IsPlaying);
            Absorb(_open, snapshot);

            if (snapshot.IsPaused && wasPlaying)
                step.Events.Add(Transport(snapshot, "transport_paused", "Paused"));
            else if (snapshot.IsPlaying && wasPaused)
                step.Events.Add(Transport(snapshot, "transport_resumed", "Resumed"));
            else if (snapshot.IsStopped && (wasPlaying || wasPaused))
            {
                step.Events.Add(Transport(snapshot, "transport_stopped", "Stopped"));
                Close(snapshot.Utc, PlayEndReasons.TransportStopped, step);
            }

            if (_open is not null)
                _open.LastWasPaused = snapshot.IsPaused;
            return step;
        }

        Credit(_open, snapshot.Utc, stillPlaying: false);
        Close(snapshot.Utc, PlayEndReasons.TrackChanged, step);
        if (snapshot.IsPlaying || snapshot.IsPaused)
            Open(snapshot, step);
        return step;
    }

    /// <summary>Close the open session immediately (active speaker dropped).</summary>
    public TrackerStep ForceClose(DateTime utc, string trigger)
    {
        var step = new TrackerStep();
        if (_open is null)
            return step;
        _pendingTrigger = trigger;
        _pendingUntil = utc.AddMinutes(1);
        _pendingSessionId = _open.Id;
        Credit(_open, utc, stillPlaying: false);
        Close(utc, PlayEndReasons.TrackChanged, step);
        return step;
    }

    private void Open(PlaySnapshot snapshot, TrackerStep step)
    {
        var session = new OpenSession
        {
            Id = _nextId++,
            TrackKey = snapshot.TrackKey,
            StartedUtc = snapshot.Utc,
            LastUtc = snapshot.Utc,
            LastWasPlaying = snapshot.IsPlaying,
            LastWasPaused = snapshot.IsPaused,
        };
        Absorb(session, snapshot);
        _open = session;
        var label = ActivityText.TrackLabel(session.Title, session.Artist, session.TrackKey);
        step.Events.Add(new ActivityEventDraft
        {
            Utc = snapshot.Utc,
            Category = ActivityCategories.Music,
            Kind = "track_started",
            Summary = $"Started: {label}",
            Zone = session.Zone,
            TrackKey = session.TrackKey,
            Title = session.Title,
            Artist = session.Artist,
        });
    }

    private void Close(DateTime utc, string endReason, TrackerStep step)
    {
        var open = _open;
        if (open is null)
            return;
        _open = null;

        var listened = Listened(open);
        var trigger = ConsumeTrigger(open.Id, utc);
        var classified = PlayOutcomeClassifier.Classify(new PlayEndFacts(
            listened, open.DurationSeconds, endReason, trigger));
        var label = ActivityText.TrackLabel(open.Title, open.Artist, open.TrackKey);
        var heard = ActivityText.FormatClock(listened);
        var length = ActivityText.FormatClock(open.DurationSeconds);

        step.Closed.Add(new ClosedSession
        {
            TrackKey = open.TrackKey,
            Uri = open.Uri,
            Title = open.Title,
            Artist = open.Artist,
            Album = open.Album,
            Zone = open.Zone,
            Source = open.Source,
            StartedUtc = open.StartedUtc,
            EndedUtc = utc,
            ListenedSeconds = listened,
            TrackSeconds = open.DurationSeconds,
            Outcome = classified.Outcome,
            Trigger = classified.Trigger,
        });
        step.Events.Add(new ActivityEventDraft
        {
            Utc = utc,
            Category = ActivityCategories.Music,
            Kind = "track_ended",
            Summary = $"Ended: {label} · {ActivityText.OutcomeLabel(classified.Outcome)} · {ActivityText.TriggerLabel(classified.Trigger)} · {heard} / {length}",
            Zone = open.Zone,
            TrackKey = open.TrackKey,
            Title = open.Title,
            Artist = open.Artist,
            Outcome = classified.Outcome,
            Trigger = classified.Trigger,
            ListenedSeconds = listened,
            TrackSeconds = open.DurationSeconds,
        });
    }

    private void ExpirePending(DateTime utc)
    {
        if (_pendingTrigger is not null && utc > _pendingUntil)
        {
            _pendingTrigger = null;
            _pendingSessionId = 0;
        }
    }

    private string? ConsumeTrigger(int sessionId, DateTime utc)
    {
        if (_pendingTrigger is null || utc > _pendingUntil || _pendingSessionId != sessionId)
            return null;
        var trigger = _pendingTrigger;
        _pendingTrigger = null;
        _pendingSessionId = 0;
        return trigger;
    }

    private static void Credit(OpenSession open, DateTime utc, bool stillPlaying)
    {
        if (utc >= open.LastUtc && open.LastWasPlaying)
        {
            var gap = (utc - open.LastUtc).TotalSeconds;
            if (gap > 0 && gap <= MaxSampleGapSeconds)
                open.PlayingSeconds += gap;
            else if (gap > MaxSampleGapSeconds && gap <= MaxSparseSpanSeconds
                     && open.PlayingSeconds < 1
                     && open.MaxPosition is null or <= 1)
                open.PlayingSeconds += gap;
        }

        if (utc >= open.LastUtc)
            open.LastUtc = utc;
        open.LastWasPlaying = stillPlaying;
    }

    private static void Absorb(OpenSession open, PlaySnapshot snapshot)
    {
        if (snapshot.PositionSeconds is > 0 and var pos)
            open.MaxPosition = Math.Max(open.MaxPosition ?? 0, pos);
        if (snapshot.DurationSeconds is > 0 and var dur)
            open.DurationSeconds = dur;
        if (!string.IsNullOrWhiteSpace(snapshot.Uri)) open.Uri = snapshot.Uri;
        if (!string.IsNullOrWhiteSpace(snapshot.Title)) open.Title = snapshot.Title!.Trim();
        if (!string.IsNullOrWhiteSpace(snapshot.Artist)) open.Artist = snapshot.Artist!.Trim();
        if (!string.IsNullOrWhiteSpace(snapshot.Album)) open.Album = snapshot.Album!.Trim();
        if (!string.IsNullOrWhiteSpace(snapshot.Zone)) open.Zone = snapshot.Zone;
        if (!string.IsNullOrWhiteSpace(snapshot.Source)) open.Source = snapshot.Source;
    }

    private static bool IsRewind(OpenSession open, PlaySnapshot snapshot) =>
        open.MaxPosition is > 8
        && snapshot.PositionSeconds is double pos
        && pos < open.MaxPosition.Value - 8;

    private static double? Listened(OpenSession open)
    {
        double? pos = open.MaxPosition is > 0 ? open.MaxPosition : null;
        double? wall = open.PlayingSeconds > 0.5 ? open.PlayingSeconds : null;
        if (pos is null) return wall;
        if (wall is null) return pos;
        // Playhead is the real "how far it got". Wall clock fills in when the
        // playhead was never reported, or when it barely moved (GENA without RelTime
        // can stick at a tiny value while polls still show the song playing).
        if (pos < 5 && wall > pos + 15)
            return wall;
        return pos;
    }

    private static bool HasIdentity(PlaySnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.TrackKey);

    private static ActivityEventDraft Transport(PlaySnapshot snapshot, string kind, string verb) => new()
    {
        Utc = snapshot.Utc,
        Category = ActivityCategories.Sonos,
        Kind = kind,
        Summary = $"{verb}: {ActivityText.TrackLabel(snapshot.Title, snapshot.Artist, snapshot.TrackKey)}",
        Zone = snapshot.Zone,
        TrackKey = snapshot.TrackKey,
        Title = snapshot.Title,
        Artist = snapshot.Artist,
    };

    private sealed class OpenSession
    {
        public int Id { get; init; }
        public required string TrackKey { get; init; }
        public string? Uri { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Zone { get; set; }
        public string? Source { get; set; }
        public DateTime StartedUtc { get; init; }
        public DateTime LastUtc { get; set; }
        public bool LastWasPlaying { get; set; }
        public bool LastWasPaused { get; set; }
        public double PlayingSeconds { get; set; }
        public double? MaxPosition { get; set; }
        public double? DurationSeconds { get; set; }
    }
}
