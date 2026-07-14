namespace HotSonos.Core.Models;

/// <summary>Where a playable entry came from, which determines how it is played.</summary>
public enum SonosFavoriteKind
{
    /// <summary>A Sonos Favorite (container "FV:2"); played via SetAVTransportURI.</summary>
    Favorite,

    /// <summary>A saved Sonos Playlist (container "SQ:"); played via enqueue + play-from-queue.</summary>
    Playlist,
}

/// <summary>
/// A playable entry the user can bind to a hotkey: a Sonos Favorite (FV:2) or a
/// saved Sonos Playlist (SQ:). <see cref="Kind"/> selects the playback path.
/// </summary>
public sealed record SonosFavorite
{
    /// <summary>DIDL item/container id, e.g. "FV:2/12" or "SQ:0".</summary>
    public required string Id { get; init; }

    /// <summary>Favorite vs. saved playlist.</summary>
    public SonosFavoriteKind Kind { get; init; } = SonosFavoriteKind.Favorite;

    /// <summary>Display title, e.g. "Morning Jazz".</summary>
    public required string Title { get; init; }

    /// <summary>
    /// The &lt;res&gt; playback URI for this favorite. Empty for "shortcut"-type
    /// favorites (e.g. Sonos Radio promo tiles), which are not directly playable.
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// The enclosed item metadata (DIDL-Lite XML, from &lt;r:resMD&gt;) passed as
    /// CurrentURIMetaData. May be empty for some favorites.
    /// </summary>
    public required string Metadata { get; init; }

    /// <summary>
    /// True when HotSonos can start this entry. Favorites need a playable &lt;res&gt;
    /// URI (SetAVTransportURI). Playlists are started by container id
    /// (x-rincon-playlist), so they only need a non-empty <see cref="Id"/> —
    /// their &lt;res&gt; is often empty or a non-playable file:// path.
    /// </summary>
    public bool IsPlayable => Kind == SonosFavoriteKind.Playlist
        ? !string.IsNullOrWhiteSpace(Id)
        : !string.IsNullOrWhiteSpace(Uri);
}
