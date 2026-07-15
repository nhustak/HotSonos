namespace HotSonos.Core.Models;

/// <summary>
/// A snapshot of what a group coordinator is currently playing, parsed from an
/// AVTransport event (or a GetPositionInfo poll).
/// </summary>
public sealed record NowPlaying
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }

    /// <summary>Absolute album-art URL on the speaker, or null when none.</summary>
    public string? AlbumArtUri { get; init; }

    /// <summary>Current track URI when present (often <c>x-file-cifs://…</c> for local library).</summary>
    public string? TrackUri { get; init; }

    /// <summary>Raw AVTransport <c>TransportStatus</c> when present (e.g. OK / ERROR_OCCURRED).</summary>
    public string? TransportStatus { get; init; }

    public SonosTransportState State { get; init; } = SonosTransportState.Unknown;

    /// <summary>True when there's no meaningful track (stopped/idle/empty).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(TrackUri);

    public string DisplayLine => IsEmpty
        ? "Nothing playing"
        : string.IsNullOrWhiteSpace(Artist) ? (Title ?? TrackUri ?? "?") : $"{Title} — {Artist}";
}
