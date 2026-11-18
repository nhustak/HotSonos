namespace HotSonos.Activity;

public static class ActivityCategories
{
    public const string Music = "music";
    public const string Sonos = "sonos";
}

/// <summary>One now-playing observation handed to <see cref="PlaySessionTracker"/>.</summary>
public sealed class PlaySnapshot
{
    public required DateTime Utc { get; init; }
    public required string TrackKey { get; init; }
    public string? Uri { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Zone { get; init; }
    public string? Source { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsPaused { get; init; }
    public bool IsStopped { get; init; }
    public double? PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
}

public sealed class ClosedSession
{
    public required string TrackKey { get; init; }
    public string? Uri { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Zone { get; init; }
    public string? Source { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DateTime EndedUtc { get; init; }
    public double? ListenedSeconds { get; init; }
    public double? TrackSeconds { get; init; }
    public required string Outcome { get; init; }
    public required string Trigger { get; init; }
}

public sealed class ActivityEventDraft
{
    public required DateTime Utc { get; init; }
    public required string Category { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public string? Detail { get; init; }
    public string? Zone { get; init; }
    public string? TrackKey { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Outcome { get; init; }
    public string? Trigger { get; init; }
    public double? ListenedSeconds { get; init; }
    public double? TrackSeconds { get; init; }
}

public sealed class TrackerStep
{
    public List<ClosedSession> Closed { get; } = [];
    public List<ActivityEventDraft> Events { get; } = [];
}
