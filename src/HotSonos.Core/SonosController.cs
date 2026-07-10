using System.Xml.Linq;
using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>
/// High-level control of one Sonos group, addressed by its <b>coordinator</b> IP.
/// All transport commands (play/pause/next/previous) and favorite playback are
/// sent here. Construct a new controller when the target group/room changes.
/// </summary>
public sealed class SonosController
{
    private readonly SonosSoapClient _soap;

    /// <summary>IP of the group coordinator this controller drives.</summary>
    public string CoordinatorIp { get; }

    /// <summary>UUID of the group coordinator (needed to address its local queue).</summary>
    public string CoordinatorUuid { get; }

    public SonosController(string coordinatorIp, string coordinatorUuid, SonosSoapClient? soap = null)
    {
        CoordinatorIp = coordinatorIp;
        CoordinatorUuid = coordinatorUuid;
        _soap = soap ?? new SonosSoapClient();
    }

    /// <summary>Convenience: build a controller targeting a zone's group coordinator.</summary>
    public static SonosController ForZone(SonosZone zone, SonosSoapClient? soap = null) =>
        new(zone.CoordinatorIpAddress, zone.CoordinatorUuid, soap);

    // ---- Transport ---------------------------------------------------------

    public Task PlayAsync(CancellationToken ct = default) =>
        InvokeAvTransport("Play", ct, ("InstanceID", "0"), ("Speed", "1"));

    public Task PauseAsync(CancellationToken ct = default) =>
        InvokeAvTransport("Pause", ct, ("InstanceID", "0"));

    public Task NextAsync(CancellationToken ct = default) =>
        InvokeAvTransport("Next", ct, ("InstanceID", "0"));

    public Task PreviousAsync(CancellationToken ct = default) =>
        InvokeAvTransport("Previous", ct, ("InstanceID", "0"));

    /// <summary>Reads the coordinator's current transport state.</summary>
    public async Task<SonosTransportState> GetTransportStateAsync(CancellationToken ct = default)
    {
        var response = await _soap.InvokeAsync(
            CoordinatorIp, SonosService.AvTransport, "GetTransportInfo",
            [new("InstanceID", "0")], ct).ConfigureAwait(false);
        return SonosTransportStateParser.Parse(SonosSoapClient.ReadValue(response, "CurrentTransportState"));
    }

    /// <summary>Toggles play/pause based on the current state. Returns the new intended state.</summary>
    public async Task<SonosTransportState> PlayPauseAsync(CancellationToken ct = default)
    {
        var state = await GetTransportStateAsync(ct).ConfigureAwait(false);
        if (state == SonosTransportState.Playing)
        {
            await PauseAsync(ct).ConfigureAwait(false);
            return SonosTransportState.PausedPlayback;
        }

        await PlayAsync(ct).ConfigureAwait(false);
        return SonosTransportState.Playing;
    }

    // ---- Favorites ---------------------------------------------------------

    /// <summary>
    /// Lists everything the user can bind to a hotkey: Sonos Favorites ("FV:2")
    /// followed by saved Sonos Playlists ("SQ:").
    /// </summary>
    public async Task<IReadOnlyList<SonosFavorite>> GetFavoritesAsync(CancellationToken ct = default)
    {
        var favorites = await BrowseAsync("FV:2", SonosFavoriteKind.Favorite, ct).ConfigureAwait(false);
        var playlists = await BrowseAsync("SQ:", SonosFavoriteKind.Playlist, ct).ConfigureAwait(false);
        return [.. favorites, .. playlists];
    }

    private async Task<IReadOnlyList<SonosFavorite>> BrowseAsync(string objectId, SonosFavoriteKind kind, CancellationToken ct)
    {
        var response = await _soap.InvokeAsync(
            CoordinatorIp, SonosService.ContentDirectory, "Browse",
            [
                new("ObjectID", objectId),
                new("BrowseFlag", "BrowseDirectChildren"),
                new("Filter", "*"),
                new("StartingIndex", "0"),
                new("RequestedCount", "200"),
                new("SortCriteria", ""),
            ], ct).ConfigureAwait(false);

        var didl = SonosSoapClient.ReadValue(response, "Result");
        return string.IsNullOrWhiteSpace(didl) ? [] : ParseFavorites(didl, kind);
    }

    /// <summary>Plays a favorite by its (case-insensitive) title. Throws if not found.</summary>
    public async Task PlayFavoriteByNameAsync(string title, CancellationToken ct = default)
    {
        var favorites = await GetFavoritesAsync(ct).ConfigureAwait(false);
        var match = favorites.FirstOrDefault(f =>
            string.Equals(f.Title, title, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new InvalidOperationException(
                $"No Sonos favorite named '{title}'. Available: {string.Join(", ", favorites.Select(f => f.Title))}");
        }

        await PlayFavoriteAsync(match, ct).ConfigureAwait(false);
    }

    /// <summary>Starts playback of a favorite or saved playlist.</summary>
    public async Task PlayFavoriteAsync(SonosFavorite favorite, CancellationToken ct = default)
    {
        if (!favorite.IsPlayable)
        {
            throw new InvalidOperationException(
                $"'{favorite.Title}' is a shortcut/container with no playable URI " +
                "(e.g. a Sonos Radio promo tile). Favorite an actual station/playlist/album in the Sonos app instead.");
        }

        if (favorite.Kind == SonosFavoriteKind.Playlist)
        {
            await PlayPlaylistAsync(favorite, ct).ConfigureAwait(false);
            return;
        }

        await _soap.InvokeAsync(
            CoordinatorIp, SonosService.AvTransport, "SetAVTransportURI",
            [
                new("InstanceID", "0"),
                new("CurrentURI", favorite.Uri),
                new("CurrentURIMetaData", favorite.Metadata),
            ], ct).ConfigureAwait(false);

        await PlayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Number of tracks sent per <c>AddMultipleURIsToQueue</c> call. Sonos'
    /// UPnP implementation silently truncates much larger batches, so this
    /// stays well under the commonly-cited safe limit.
    /// </summary>
    private const int EnqueueBatchSize = 16;

    /// <summary>
    /// Shuffles the entire local Music Library across the group.
    /// </summary>
    /// <remarks>
    /// The device's own <c>SetPlayMode SHUFFLE</c> derives its play order
    /// deterministically from the queue's content, so re-enqueuing the same
    /// "all tracks" container and flipping to SHUFFLE produces the *same*
    /// shuffled order every time. To get a genuinely different order per
    /// invocation, the shuffle happens here on the client: the full track
    /// list is browsed, randomized, and enqueued pre-shuffled with plain
    /// NORMAL play mode.
    /// </remarks>
    public async Task ShuffleMusicLibraryAsync(CancellationToken ct = default)
    {
        var tracks = (await BrowseAllTracksAsync(ct).ConfigureAwait(false)).ToList();
        if (tracks.Count == 0)
            throw new InvalidOperationException("No tracks found in the local Music Library.");

        Shuffle(tracks);

        await InvokeAvTransport("RemoveAllTracksFromQueue", ct, ("InstanceID", "0"));

        foreach (var batch in tracks.Chunk(EnqueueBatchSize))
        {
            await InvokeAvTransport("AddMultipleURIsToQueue", ct,
                ("InstanceID", "0"),
                ("UpdateID", "0"),
                ("NumberOfURIs", batch.Length.ToString()),
                ("EnqueuedURIs", string.Join(' ', batch.Select(t => t.Uri))),
                ("EnqueuedURIsMetaData", string.Join(' ', batch.Select(t => BuildItemMetadata(t.Item)))),
                ("ContainerURI", ""),
                ("ContainerMetaData", ""),
                ("DesiredFirstTrackNumberEnqueued", "0"),
                ("EnqueueAsNext", "0")).ConfigureAwait(false);
        }

        await InvokeAvTransport("SetAVTransportURI", ct,
            ("InstanceID", "0"),
            ("CurrentURI", $"x-rincon-queue:{CoordinatorUuid}#0"),
            ("CurrentURIMetaData", "")).ConfigureAwait(false);

        await InvokeAvTransport("SetPlayMode", ct, ("InstanceID", "0"), ("NewPlayMode", "NORMAL")).ConfigureAwait(false);
        await PlayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Browses every track under the local Music Library ("A:TRACKS"), paginating as needed.</summary>
    private async Task<IReadOnlyList<(string Uri, XElement Item)>> BrowseAllTracksAsync(CancellationToken ct)
    {
        var tracks = new List<(string Uri, XElement Item)>();
        var startingIndex = 0;

        while (true)
        {
            var response = await _soap.InvokeAsync(
                CoordinatorIp, SonosService.ContentDirectory, "Browse",
                [
                    new("ObjectID", "A:TRACKS"),
                    new("BrowseFlag", "BrowseDirectChildren"),
                    new("Filter", "*"),
                    new("StartingIndex", startingIndex.ToString()),
                    new("RequestedCount", "200"),
                    new("SortCriteria", ""),
                ], ct).ConfigureAwait(false);

            var didl = SonosSoapClient.ReadValue(response, "Result");
            var numberReturned = int.Parse(SonosSoapClient.ReadValue(response, "NumberReturned") ?? "0");
            var totalMatches = int.Parse(SonosSoapClient.ReadValue(response, "TotalMatches") ?? "0");

            if (!string.IsNullOrWhiteSpace(didl))
            {
                var doc = XDocument.Parse(didl);
                foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
                {
                    var uri = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "res")?.Value;
                    if (!string.IsNullOrEmpty(uri))
                        tracks.Add((uri, item));
                }
            }

            startingIndex += numberReturned;
            if (numberReturned == 0 || startingIndex >= totalMatches)
                break;
        }

        return tracks;
    }

    /// <summary>
    /// Wraps one browsed track &lt;item&gt; element in its own standalone
    /// DIDL-Lite document. <c>EnqueuedURIsMetaData</c> takes one such
    /// document per track, space-joined in the same order as the space-joined
    /// <c>EnqueuedURIs</c> list.
    /// </summary>
    private static string BuildItemMetadata(XElement item) =>
        "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" " +
        "xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
        item.ToString(SaveOptions.DisableFormatting) +
        "</DIDL-Lite>";

    /// <summary>Fisher-Yates shuffle in place, using a fresh random sequence each call.</summary>
    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Plays a saved Sonos Playlist by enqueuing its container (the server
    /// expands all tracks in one call), then playing the queue.
    /// </summary>
    private async Task PlayPlaylistAsync(SonosFavorite playlist, CancellationToken ct)
    {
        await ReplaceQueueWithContainerAsync(playlist.Id, ct);
        await PlayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the queue, enqueues a content-directory container by id via the
    /// x-rincon-playlist scheme (server-side expansion), and points the
    /// coordinator at its local queue. Caller starts playback.
    /// </summary>
    private async Task ReplaceQueueWithContainerAsync(string containerId, CancellationToken ct)
    {
        await InvokeAvTransport("RemoveAllTracksFromQueue", ct, ("InstanceID", "0"));

        await InvokeAvTransport("AddURIToQueue", ct,
            ("InstanceID", "0"),
            ("EnqueuedURI", $"x-rincon-playlist:{CoordinatorUuid}#{containerId}"),
            ("EnqueuedURIMetaData", ""),
            ("DesiredFirstTrackNumberEnqueued", "0"),
            ("EnqueueAsNext", "0"));

        await InvokeAvTransport("SetAVTransportURI", ct,
            ("InstanceID", "0"),
            ("CurrentURI", $"x-rincon-queue:{CoordinatorUuid}#0"),
            ("CurrentURIMetaData", ""));
    }

    // ---- Internals ---------------------------------------------------------

    private Task InvokeAvTransport(string action, CancellationToken ct, params (string Name, string Value)[] args) =>
        _soap.InvokeAsync(
            CoordinatorIp, SonosService.AvTransport, action,
            args.Select(a => new KeyValuePair<string, string>(a.Name, a.Value)), ct);

    /// <summary>Parses DIDL-Lite markup into <see cref="SonosFavorite"/> entries.</summary>
    internal static IReadOnlyList<SonosFavorite> ParseFavorites(string didl, SonosFavoriteKind kind)
    {
        var doc = XDocument.Parse(didl);
        var favorites = new List<SonosFavorite>();

        // Favorites are <item>; saved playlists are <container>.
        foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName is "item" or "container"))
        {
            var id = (string?)entry.Attribute("id") ?? "";
            var title = ChildValue(entry, "title") ?? "(untitled)";

            // Direct <res> is the playback URI. Shortcut-type favorites (Sonos Radio
            // promos) leave it empty; we still list them but mark them non-playable.
            var uri = ChildValue(entry, "res") ?? "";

            // resMD holds the enclosed item's metadata; reading .Value un-escapes it once.
            var metadata = ChildValue(entry, "resMD") ?? "";

            favorites.Add(new SonosFavorite
            {
                Id = id,
                Kind = kind,
                Title = title,
                Uri = uri,
                Metadata = metadata,
            });
        }

        return favorites;
    }

    private static string? ChildValue(XElement parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
}
