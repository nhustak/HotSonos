namespace HotSonos.App.Library;

/// <summary>One audio file row in the rebuildable library cache (not the SoT — tags live in files).</summary>
public sealed class LibraryTrack
{
    public string Path { get; set; } = "";
    public string Root { get; set; } = "";
    public string? RelativePath { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    public int? Year { get; set; }
    public long? DurationMs { get; set; }
    /// <summary>From file custom tag <c>HOTSONOS_TEMPO</c> when present.</summary>
    public string? Tempo { get; set; }
    public double? Bpm { get; set; }

    /// <summary>
    /// Optional linked twin under <c>MasterLibraryRoot</c> (cache only — dual-write target).
    /// Preserved across rescans; set on successful auto-match or manual link.
    /// </summary>
    public string? MasterPath { get; set; }

    // ---- Audio technical (TagLib properties) --------------------------------
    public string? Codec { get; set; }
    public int? SampleRateHz { get; set; }
    public int? BitsPerSample { get; set; }
    public int? Channels { get; set; }
    /// <summary>Approximate bitrate in kbps when known (often lossy only).</summary>
    public int? BitrateKbps { get; set; }
    /// <summary>Heuristic: within Sonos local-library limits (see <see cref="SonosPlayability"/>).</summary>
    public bool SonosPlayable { get; set; } = true;
    /// <summary>Why not playable, or soft notes (unknown props).</summary>
    public string? SonosPlayIssue { get; set; }

    public long FileSize { get; set; }
    public DateTime FileMtimeUtc { get; set; }
    public DateTime LastScannedUtc { get; set; }

    public string AudioFormatLabel => SonosPlayability.FormatLabel(this);
}

/// <summary>Snapshot of cache + scan progress for UI / MCP.</summary>
public sealed class LibraryStatus
{
    public bool IsScanning { get; init; }
    public int TrackCount { get; init; }
    public int SonosUnplayableCount { get; init; }
    public int RootsConfigured { get; init; }
    public IReadOnlyList<string> Roots { get; init; } = [];
    public string? MasterRoot { get; init; }
    public string? DatabasePath { get; init; }
    public DateTime? LastScanStartedUtc { get; init; }
    public DateTime? LastScanFinishedUtc { get; init; }
    public string? LastScanError { get; init; }
    public int LastScanFilesSeen { get; init; }
    public int LastScanFilesUpdated { get; init; }
    public int LastScanFilesSkippedUnchanged { get; init; }
    public int LastScanFilesRemoved { get; init; }
    public int LastScanErrors { get; init; }
    public string? Phase { get; init; }
    public string Note { get; init; } =
        "SQLite is a rebuildable cache. SonosPlayable is a format heuristic (≤48 kHz, FLAC ≤24-bit, etc.), not a live speaker report.";
}
