namespace HotSonos.Remote.Protocol;

/// <summary>
/// Whether Quick tag can write the playing track. The same rules the tray overlay uses:
/// a library file under the configured roots, matched from the Sonos track URI.
/// </summary>
public static class NowPlayingTagTarget
{
    public const string NothingPlaying = "Nothing playing.";

    public const string NoUri = "No track URI from Sonos.";

    public const string NotInLibrary =
        "Track not in library cache (streaming/radio, or run a rescan). Only Sonos Music Library files under configured roots can be tagged.";

    public const string LibraryUnavailable = "Library service not available.";

    public static (bool CanTag, string? Path, string? Message) Resolve(bool empty, bool missingUri, string? libraryPath)
    {
        if (empty)
            return (false, null, NothingPlaying);
        if (missingUri)
            return (false, null, NoUri);
        if (string.IsNullOrWhiteSpace(libraryPath))
            return (false, null, NotInLibrary);
        return (true, libraryPath.Trim(), null);
    }
}
