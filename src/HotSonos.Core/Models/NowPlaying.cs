namespace HotSonos.Core.Models;

/// <summary>Where the bits are coming from (library SMB host vs cloud stream vs radio).</summary>
public enum NowPlayingSourceKind
{
    Unknown,
    Empty,
    /// <summary>x-file-cifs / x-file-smb music library share.</summary>
    LibrarySmb,
    Spotify,
    SonosRadioOrStream,
    Deezer,
    Amazon,
    Apple,
    Pandora,
    TuneIn,
    LineInOrTv,
    OtherStream,
}

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

    /// <summary>1-based index of the current queue track when Sonos reports it.</summary>
    public int? CurrentTrack { get; init; }

    /// <summary>Total tracks in the active queue when Sonos reports it.</summary>
    public int? NumberOfTracks { get; init; }

    public SonosTransportState State { get; init; } = SonosTransportState.Unknown;

    /// <summary>
    /// True when this many tracks or fewer remain after the current one
    /// (time to top-up). Default 4 matches the previous hard-coded behavior.
    /// </summary>
    public bool IsNearQueueEnd(int remainingInclusive = 4)
    {
        if (CurrentTrack is not int cur || NumberOfTracks is not int total || total <= 0)
            return false;
        remainingInclusive = Math.Clamp(remainingInclusive, 1, 50);
        return (total - cur) <= remainingInclusive;
    }

    /// <summary>True when there's no meaningful track (stopped/idle/empty).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(TrackUri);

    public string DisplayLine => IsEmpty
        ? "Nothing playing"
        : string.IsNullOrWhiteSpace(Artist) ? (Title ?? TrackUri ?? "?") : $"{Title} — {Artist}";

    /// <summary>Coarse source kind derived from <see cref="TrackUri"/>.</summary>
    public NowPlayingSourceKind SourceKind => ClassifySource(TrackUri, IsEmpty);

    /// <summary>
    /// One-line human source for UI: e.g. <c>Library · NAS 192.168.1.111</c>,
    /// <c>Library · PC 192.168.1.129</c>, <c>Sonos Radio / stream</c>, <c>Spotify</c>.
    /// </summary>
    public string SourceLabel => FormatSourceLabel(TrackUri, IsEmpty);

    /// <summary>Short host or scheme detail for tooltips (may be empty).</summary>
    public string SourceDetail => FormatSourceDetail(TrackUri);

    public static NowPlayingSourceKind ClassifySource(string? trackUri, bool isEmpty = false)
    {
        if (isEmpty && string.IsNullOrWhiteSpace(trackUri))
            return NowPlayingSourceKind.Empty;
        if (string.IsNullOrWhiteSpace(trackUri))
            return NowPlayingSourceKind.Unknown;

        var u = trackUri.Trim();
        var lower = u.ToLowerInvariant();

        if (lower.StartsWith("x-file-cifs:", StringComparison.Ordinal)
            || lower.StartsWith("x-file-smb:", StringComparison.Ordinal)
            || lower.StartsWith("x-rincon-file:", StringComparison.Ordinal)
            || lower.Contains("x-file-cifs", StringComparison.Ordinal)
            || lower.StartsWith(@"\\", StringComparison.Ordinal)
            // Relative / path-style library keys Sonos sometimes surfaces
            || lower.StartsWith("./", StringComparison.Ordinal)
            || lower.EndsWith(".flac", StringComparison.Ordinal)
            || lower.EndsWith(".mp3", StringComparison.Ordinal)
            || lower.EndsWith(".m4a", StringComparison.Ordinal)
            || lower.EndsWith(".wav", StringComparison.Ordinal)
            || lower.EndsWith(".aiff", StringComparison.Ordinal)
            || lower.EndsWith(".aif", StringComparison.Ordinal))
            return NowPlayingSourceKind.LibrarySmb;

        if (lower.Contains("spotify", StringComparison.Ordinal)
            || lower.StartsWith("x-sonos-spotify:", StringComparison.Ordinal)
            || lower.Contains("spotify.com", StringComparison.Ordinal))
            return NowPlayingSourceKind.Spotify;

        if (lower.Contains("dzr", StringComparison.Ordinal)
            || lower.Contains("deezer", StringComparison.Ordinal))
            return NowPlayingSourceKind.Deezer;

        if (lower.Contains("amazon", StringComparison.Ordinal)
            || lower.Contains("prime", StringComparison.Ordinal) && lower.Contains("music", StringComparison.Ordinal))
            return NowPlayingSourceKind.Amazon;

        if (lower.Contains("apple", StringComparison.Ordinal) && lower.Contains("music", StringComparison.Ordinal)
            || lower.Contains("itunes", StringComparison.Ordinal))
            return NowPlayingSourceKind.Apple;

        if (lower.Contains("pandora", StringComparison.Ordinal))
            return NowPlayingSourceKind.Pandora;

        if (lower.Contains("tunein", StringComparison.Ordinal)
            || lower.StartsWith("x-rincon-mp3radio:", StringComparison.Ordinal)
            || lower.StartsWith("aac://", StringComparison.Ordinal)
            || lower.StartsWith("hls-radio:", StringComparison.Ordinal))
            return NowPlayingSourceKind.TuneIn;

        if (lower.StartsWith("x-rincon-stream:", StringComparison.Ordinal)
            || lower.StartsWith("x-sonos-htastream:", StringComparison.Ordinal)
            || lower.Contains("spdif", StringComparison.Ordinal)
            || lower.Contains("tv:", StringComparison.Ordinal))
            return NowPlayingSourceKind.LineInOrTv;

        if (lower.StartsWith("x-sonosapi-", StringComparison.Ordinal)
            || lower.StartsWith("x-sonosprog-", StringComparison.Ordinal)
            || lower.StartsWith("x-sonos-http:", StringComparison.Ordinal)
            || lower.StartsWith("http://", StringComparison.Ordinal)
            || lower.StartsWith("https://", StringComparison.Ordinal)
            || lower.Contains("sonos-radio", StringComparison.Ordinal)
            || lower.Contains(":sd.mp4", StringComparison.Ordinal))
            return NowPlayingSourceKind.SonosRadioOrStream;

        return NowPlayingSourceKind.OtherStream;
    }

    public static string FormatSourceLabel(string? trackUri, bool isEmpty = false)
    {
        var kind = ClassifySource(trackUri, isEmpty);
        return kind switch
        {
            NowPlayingSourceKind.Empty => "",
            NowPlayingSourceKind.LibrarySmb => FormatLibraryLabel(trackUri),
            NowPlayingSourceKind.Spotify => "Spotify",
            NowPlayingSourceKind.Deezer => "Deezer / Sonos Radio",
            NowPlayingSourceKind.Amazon => "Amazon Music",
            NowPlayingSourceKind.Apple => "Apple Music",
            NowPlayingSourceKind.Pandora => "Pandora",
            NowPlayingSourceKind.TuneIn => "TuneIn / radio URL",
            NowPlayingSourceKind.LineInOrTv => "Line-in / TV",
            NowPlayingSourceKind.SonosRadioOrStream => "Sonos Radio / stream",
            NowPlayingSourceKind.OtherStream => "Stream / other",
            _ => string.IsNullOrWhiteSpace(trackUri) ? "Unknown source" : "Unknown source",
        };
    }

    public static string FormatSourceDetail(string? trackUri)
    {
        if (string.IsNullOrWhiteSpace(trackUri))
            return "";
        return trackUri.Length <= 120 ? trackUri : trackUri[..117] + "...";
    }

    private static string FormatLibraryLabel(string? trackUri)
    {
        var host = TryExtractSmbHost(trackUri);
        if (string.IsNullOrWhiteSpace(host))
            return "Library (SMB)";

        var role = ClassifySmbHost(host);
        return $"Library · {role} {host}";
    }

    /// <summary>NAS / PC / other labels for known hosts on this network.</summary>
    public static string ClassifySmbHost(string host)
    {
        var h = host.Trim().TrimEnd('.');
        if (h.Equals("192.168.1.111", StringComparison.OrdinalIgnoreCase)
            || h.Equals("stormnas", StringComparison.OrdinalIgnoreCase)
            || h.Equals("storm", StringComparison.OrdinalIgnoreCase))
            return "NAS";

        if (h.Equals("192.168.1.129", StringComparison.OrdinalIgnoreCase)
            || h.Equals("enterprise", StringComparison.OrdinalIgnoreCase)
            || h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return "PC";

        // Heuristic: .local / short names without digits → PC hostname
        if (!h.Contains('.') && h.Any(char.IsLetter))
            return "PC?";

        return "host";
    }

    public static string? TryExtractSmbHost(string? trackUri)
    {
        if (string.IsNullOrWhiteSpace(trackUri))
            return null;

        var u = trackUri.Trim();
        // x-file-cifs://HOST/share/path  or  x-file-cifs://HOST/path
        const string cifs = "x-file-cifs://";
        const string smb = "x-file-smb://";
        string rest;
        if (u.StartsWith(cifs, StringComparison.OrdinalIgnoreCase))
            rest = u[cifs.Length..];
        else if (u.StartsWith(smb, StringComparison.OrdinalIgnoreCase))
            rest = u[smb.Length..];
        else if (u.StartsWith(@"\\"))
            rest = u[2..];
        else
            return null;

        // user@host or host
        var slash = rest.IndexOfAny(['/', '\\']);
        var hostPart = slash < 0 ? rest : rest[..slash];
        var at = hostPart.LastIndexOf('@');
        if (at >= 0 && at < hostPart.Length - 1)
            hostPart = hostPart[(at + 1)..];
        hostPart = Uri.UnescapeDataString(hostPart).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(hostPart) ? null : hostPart;
    }
}
