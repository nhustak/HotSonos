namespace HotSonos.Activity;

public sealed class SongStatRow
{
    public required string TrackKey { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public int PlayCount { get; init; }
    public int SkipCount { get; init; }
    public DateTime? LastPlayedUtc { get; init; }
    public string LastPlayedLocal { get; init; } = "";
}

public sealed class PlaySessionRow
{
    public required string TrackKey { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Zone { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTime StartedUtc { get; init; }
    public string WhenLocal { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string OutcomeLabel { get; init; } = "";
    public string Trigger { get; init; } = "";
    public string TriggerLabel { get; init; } = "";
    public string Listened { get; init; } = "";
    public string Length { get; init; } = "";
    public double? ListenedSeconds { get; init; }
    public double? TrackSeconds { get; init; }
}

public sealed class RankedSongRow
{
    public required string TrackKey { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public int Count { get; init; }
    public string Label { get; init; } = "";
}

public sealed class MusicReportData
{
    public IReadOnlyList<PlaySessionRow> Plays { get; init; } = [];
    public IReadOnlyList<RankedSongRow> MostPlayed { get; init; } = [];
    public IReadOnlyList<RankedSongRow> MostSkipped { get; init; } = [];
    public IReadOnlyList<RankedSongRow> RecentlyPlayed { get; init; } = [];
}

public sealed class ActivityEventRow
{
    public long Id { get; init; }
    public DateTime Utc { get; init; }
    public string WhenLocal { get; init; } = "";
    public string Category { get; init; } = "";
    public string CategoryLabel { get; init; } = "";
    public string Kind { get; init; } = "";
    public string KindLabel { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Zone { get; init; } = "";
}
