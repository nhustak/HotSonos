using HotSonos.Activity;
using HotSonos.Core.Models;

namespace HotSonos.App.Services;

/// <summary>Maps a now-playing snapshot into the activity tracker's input. Does not touch shuffle history.</summary>
internal static class ActivitySnapshots
{
    public static PlaySnapshot From(NowPlaying np, string? zone) => new()
    {
        Utc = DateTime.UtcNow,
        TrackKey = Key(np),
        Uri = np.TrackUri,
        Title = np.Title,
        Artist = np.Artist,
        Album = np.Album,
        Zone = zone,
        Source = Source(np),
        IsPlaying = np.State is SonosTransportState.Playing or SonosTransportState.Transitioning,
        IsPaused = np.State is SonosTransportState.PausedPlayback,
        IsStopped = np.State is SonosTransportState.Stopped,
        PositionSeconds = np.Position is TimeSpan p && p > TimeSpan.Zero ? p.TotalSeconds : null,
        DurationSeconds = np.Duration is TimeSpan d && d > TimeSpan.Zero ? d.TotalSeconds : null,
    };

    /// <summary>
    /// Library and per-track services (Spotify and the like) use the URI.
    /// Radio and other streams reuse one URI, so the title is part of the identity.
    /// </summary>
    public static string Key(NowPlaying np)
    {
        var key = PlayHistoryStore.NormalizeKey(np.TrackUri);
        if (np.SourceKind is NowPlayingSourceKind.LibrarySmb || NowPlaying.UsesPerTrackUri(np.SourceKind))
            return key;

        var title = (np.Title ?? "").Trim().ToLowerInvariant();
        if (title.Length == 0)
            return key;
        if (key.Length == 0)
            return "title:" + title;
        return key + "\n" + title;
    }

    private static string Source(NowPlaying np) => np.SourceKind switch
    {
        NowPlayingSourceKind.LibrarySmb => "library",
        NowPlayingSourceKind.Spotify => "spotify",
        NowPlayingSourceKind.Deezer or NowPlayingSourceKind.Amazon
            or NowPlayingSourceKind.Apple or NowPlayingSourceKind.Pandora => "stream",
        NowPlayingSourceKind.TuneIn or NowPlayingSourceKind.SonosRadioOrStream
            or NowPlayingSourceKind.OtherStream => "radio",
        NowPlayingSourceKind.LineInOrTv => "linein",
        NowPlayingSourceKind.Empty => "empty",
        _ => "other",
    };
}
