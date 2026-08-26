namespace HotSonos.Core;

/// <summary>Controls client-side library shuffle (queue build).</summary>
public sealed class ShuffleOptions
{
    /// <summary>
    /// How many tracks to put on the Sonos queue. Keep this modest so the queue
    /// ends sooner and can be rebuilt with updated play-history (default 80 ≈ a few hours).
    /// </summary>
    public int MaxQueueTracks { get; init; } = 80;

    /// <summary>
    /// URIs that must not appear in the new queue if enough other tracks exist
    /// (typically tracks actually played recently).
    /// </summary>
    public IReadOnlyCollection<string>? ExcludeUris { get; init; }

    /// <summary>
    /// Last-played times for <see cref="ExcludeUris"/> keys (any URI form).
    /// When the unheard pool is too small, older plays may slide back in;
    /// anything inside <see cref="HardRecentWindow"/> does not, unless every
    /// track in scope was heard that recently.
    /// </summary>
    public IReadOnlyDictionary<string, DateTime>? PlayedAtUtc { get; init; }

    /// <summary>Never replay inside this window while any older track exists. Default 24 hours.</summary>
    public TimeSpan HardRecentWindow { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// When set, only tracks whose path falls under one of these prefixes are eligible
    /// (daily library folders). UNC or x-file-cifs; compared via <see cref="SonosController.NormalizeTrackKey"/>.
    /// Null/empty = entire Sonos Music Library (A:TRACKS).
    /// </summary>
    public IReadOnlyCollection<string>? IncludePathPrefixes { get; init; }

    /// <summary>
    /// When true, append to the existing queue instead of clearing it (top-up).
    /// </summary>
    public bool AppendToQueue { get; init; }

    /// <summary>Prefer not placing the same artist twice in a row when easy.</summary>
    public bool ArtistSpread { get; init; } = true;
}

/// <summary>Outcome of a client-side library shuffle.</summary>
public sealed class ShuffleResult
{
    public int Browsed { get; init; }
    public int Enqueued { get; init; }
    public int ExcludedCount { get; init; }
    public int CandidateCount { get; init; }
    /// <summary>Older-than-24h history tracks added back because the unheard pool was short.</summary>
    public int SlidBackCount { get; init; }
    /// <summary>Tracks removed because they were outside IncludePathPrefixes.</summary>
    public int ScopeFilteredCount { get; init; }
    public bool Appended { get; init; }
    public IReadOnlyList<string> EnqueuedUris { get; init; } = [];
}
