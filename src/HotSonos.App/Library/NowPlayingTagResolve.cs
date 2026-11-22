using HotSonos.Core.Models;

namespace HotSonos.App.Library;

/// <summary>
/// Maps the playing Sonos track to a library file the Quick tag hotkey can write.
/// Streaming and radio stay untagged. The Control button and the phone use the same messages.
/// </summary>
public static class NowPlayingTagResolve
{
    public const string NothingPlaying = "Nothing playing.";
    public const string NoUri = "No track URI from Sonos.";
    public const string NoLibrary = "Library service not available.";
    public const string NotInCache =
        "Track not in library cache (streaming/radio, or run a rescan). " +
        "Only Sonos Music Library files under configured roots can be tagged.";

    public static (LibraryTrack? Track, string? Message) Resolve(NowPlaying? np, LibraryService? library)
    {
        if (library is null)
            return (null, NoLibrary);
        if (np is null || np.IsEmpty)
            return (null, NothingPlaying);
        if (string.IsNullOrWhiteSpace(np.TrackUri))
            return (null, NoUri);

        var track = library.FindBySonosUri(np.TrackUri);
        return track is null ? (null, NotInCache) : (track, null);
    }
}
