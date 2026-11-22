namespace HotSonos.Remote.Protocol;

/// <summary>What the speakers are doing. Published separately from the watch-facing state.</summary>
public enum MediaControlPlayback
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>A transport button on the media session.</summary>
public enum MediaTransportRequest
{
    Play,
    Pause,
    Toggle,
    Next,
    Previous,
}

/// <summary>
/// Now-playing snapshot the Android media session publishes.
/// Built only from <see cref="RemoteStatusResponse"/> so the mapping can be tested without Android.
/// </summary>
public sealed record MediaControlState(
    string Title,
    string Artist,
    string Album,
    MediaControlPlayback Playback,
    int VolumePercent,
    bool HasVolume,
    bool Muted,
    bool ShowArt,
    string? ArtToken,
    int? PositionSeconds,
    int? DurationSeconds)
{
    /// <summary>
    /// State written into the session. Stopped stays paused so a watch and the lock screen
    /// keep a play button instead of dropping the session.
    /// </summary>
    public MediaControlPlayback PublishedPlayback =>
        Playback == MediaControlPlayback.Playing
            ? MediaControlPlayback.Playing
            : MediaControlPlayback.Paused;

    public long PositionMs => PositionSeconds is int seconds and >= 0 ? seconds * 1000L : 0;

    public long DurationMs => DurationSeconds is int seconds and > 0 ? seconds * 1000L : 0;

    public float PlaybackSpeed => Playback == MediaControlPlayback.Playing ? 1f : 0f;

    public bool SameFace(MediaControlState other) =>
        Title == other.Title
        && Artist == other.Artist
        && Album == other.Album
        && PublishedPlayback == other.PublishedPlayback
        && Muted == other.Muted
        && ShowArt == other.ShowArt
        && ArtToken == other.ArtToken;
}

/// <summary>
/// Maps a status poll onto the media session: title, play/pause, and absolute volume 0–100.
/// The Android service applies this; it does not decide the rules itself.
/// </summary>
public static class MediaControlMapping
{
    public const int VolumeMax = 100;

    /// <summary>Cap on a speaker art download. Larger responses are ignored.</summary>
    public const int MaxArtBytes = 2 * 1024 * 1024;

    public const string PreferenceKey = "hotsonos.media.controls";

    /// <summary>Missing preference is on. An explicit false is off.</summary>
    public static bool PreferenceEnabled(bool? stored) => stored ?? true;

    public static MediaControlState FromStatus(RemoteStatusResponse? status)
    {
        var now = status?.NowPlaying;
        var title = string.IsNullOrWhiteSpace(now?.Title) ? "Nothing playing" : now!.Title!.Trim();
        return new MediaControlState(
            title,
            now?.Artist?.Trim() ?? "",
            now?.Album?.Trim() ?? "",
            MapPlayback(now?.State),
            ClampVolume(status?.Volume),
            status?.Volume is not null,
            status?.Muted == true,
            now?.HasArt == true && !string.IsNullOrWhiteSpace(now.ArtRevision),
            string.IsNullOrWhiteSpace(now?.ArtRevision) ? null : now!.ArtRevision.Trim(),
            now?.PositionSeconds,
            now?.DurationSeconds);
    }

    /// <summary>
    /// PC status uses the transport enum name (<c>Playing</c>, <c>PausedPlayback</c>, <c>Transitioning</c>).
    /// A raw UPnP token (<c>PLAYING</c>, <c>PAUSED_PLAYBACK</c>) maps the same way.
    /// </summary>
    public static MediaControlPlayback MapPlayback(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return MediaControlPlayback.Stopped;

        var token = state.Trim()
            .Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        if (token.Equals("Playing", StringComparison.OrdinalIgnoreCase)
            || token.Equals("Transitioning", StringComparison.OrdinalIgnoreCase))
            return MediaControlPlayback.Playing;
        if (token.Equals("Paused", StringComparison.OrdinalIgnoreCase)
            || token.Equals("PausedPlayback", StringComparison.OrdinalIgnoreCase)
            || token.Equals("Pause", StringComparison.OrdinalIgnoreCase))
            return MediaControlPlayback.Paused;
        return MediaControlPlayback.Stopped;
    }

    public static int ClampVolume(int? percent) =>
        percent is int value ? Math.Clamp(value, 0, VolumeMax) : 0;

    /// <summary>
    /// Relative nudge on the 0–100 scale. Zero direction is not a change.
    /// Absolute sets use <see cref="ClampVolume"/> instead.
    /// </summary>
    public static int? AdjustVolume(int current, int direction)
    {
        if (direction == 0)
            return null;
        var delta = direction > 0 ? 1 : -1;
        return Math.Clamp(ClampVolume(current) + delta, 0, VolumeMax);
    }

    /// <summary>
    /// Play while playing, and pause while not playing, are not sent.
    /// The PC route only has a toggle, so a stale extra press would reverse the speaker.
    /// </summary>
    public static bool ShouldSendPlayPause(MediaControlPlayback actual, MediaTransportRequest request) =>
        request switch
        {
            MediaTransportRequest.Play => actual != MediaControlPlayback.Playing,
            MediaTransportRequest.Pause => actual == MediaControlPlayback.Playing,
            MediaTransportRequest.Toggle => true,
            _ => false,
        };

    public static string NotificationText(MediaControlState state)
    {
        var body = (state.Artist, state.Album) switch
        {
            ("", "") => "HotSonos",
            (var artist, "") => artist,
            ("", var album) => album,
            (var artist, var album) => artist + " — " + album,
        };
        return state.Muted ? body + " · muted" : body;
    }

    /// <summary>Standalone art is the speaker URL. PC mode stores a short hash and the bytes come from the PC.</summary>
    public static bool IsDirectArtUri(string? token)
    {
        if (!Uri.TryCreate(token, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        return string.IsNullOrEmpty(uri.UserInfo);
    }
}
