using HotSonos.App.Models;
using HotSonos.Core;
using HotSonos.Core.Models;

namespace HotSonos.App.Services;

/// <summary>A selectable target: one Sonos group, named the way the Sonos app names it.</summary>
public sealed record SonosGroup(
    string DisplayName,
    string CoordinatorRoom,
    string CoordinatorUuid,
    string CoordinatorIp,
    int MemberCount)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// App-facing wrapper over the Core UPnP client. Holds the discovered topology
/// as groups and turns <see cref="HotsonosAction"/> into Sonos commands. Cheap
/// to call repeatedly; discovery is cached until refreshed.
/// </summary>
public sealed class SonosManager
{
    private readonly SonosSoapClient _soap = new();
    private readonly SonosDiscovery _discovery;

    private IReadOnlyList<SonosZone> _zones = [];
    private SonosController? _controller;

    public SonosManager() => _discovery = new SonosDiscovery(_soap);

    /// <summary>Discovered groups, largest first (so "All Speakers" leads).</summary>
    public IReadOnlyList<SonosGroup> Groups { get; private set; } = [];

    /// <summary>Coordinator room name of the active group; the persisted target key.</summary>
    public string? ActiveRoom { get; private set; }

    /// <summary>Re-discovers the topology and (re)resolves the active group's controller.</summary>
    public async Task RefreshAsync(string? preferredRoom = null, CancellationToken ct = default)
    {
        _zones = await _discovery.DiscoverZonesAsync(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        RebuildGroups();

        var desired = preferredRoom ?? ActiveRoom;
        if (desired is null || !Groups.Any(g => ContainsRoom(g, desired)))
            desired = Groups.FirstOrDefault()?.CoordinatorRoom;

        ActiveRoom = desired;
        RebuildController();
    }

    /// <summary>Points subsequent commands at the group whose coordinator room is <paramref name="room"/>.</summary>
    public void SetActiveRoom(string room)
    {
        ActiveRoom = room;
        RebuildController();
    }

    /// <summary>Display name of the active group, for tray/menu labels.</summary>
    public string? ActiveGroupLabel =>
        Groups.FirstOrDefault(g => string.Equals(g.CoordinatorRoom, ActiveRoom, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? ActiveRoom;

    public async Task<IReadOnlyList<SonosFavorite>> GetFavoritesAsync(CancellationToken ct = default) =>
        _controller is null ? [] : await _controller.GetFavoritesAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Executes an action and returns a short toast string (or null to show nothing).
    /// Throws on transport/network errors so the caller can surface them.
    /// </summary>
    public async Task<string?> ExecuteAsync(HotsonosAction action, AppSettings settings, CancellationToken ct = default)
    {
        if (_controller is null)
            throw new InvalidOperationException("No Sonos room is selected. Open HotSonos and pick a room.");

        var slot = action.FavoriteSlotIndex();
        if (slot >= 0)
        {
            var name = settings.FavoriteSlots[slot].FavoriteName;
            if (string.IsNullOrWhiteSpace(name))
                return $"Favorite slot {slot + 1} is empty";
            await _controller.PlayFavoriteByNameAsync(name, ct).ConfigureAwait(false);
            return $"▶ {name}";
        }

        switch (action)
        {
            case HotsonosAction.PlayPause:
                var state = await _controller.PlayPauseAsync(ct).ConfigureAwait(false);
                return state == SonosTransportState.Playing ? "▶ Playing" : "⏸ Paused";
            case HotsonosAction.Next:
                await _controller.NextAsync(ct).ConfigureAwait(false);
                return "⏭ Next";
            case HotsonosAction.Previous:
                await _controller.PreviousAsync(ct).ConfigureAwait(false);
                return "⏮ Previous";
            case HotsonosAction.ShuffleLibrary:
                await GroupAllSpeakersAsync(ct).ConfigureAwait(false);
                await _controller.ShuffleMusicLibraryAsync(ct).ConfigureAwait(false);
                return "🔀 Shuffling library → all speakers";
            default:
                return null;
        }
    }

    /// <summary>
    /// Pulls every visible player into the active group's coordinator so that
    /// subsequent playback covers all speakers. Idempotent; tolerates individual
    /// join failures. The coordinator's IP/UUID are unchanged, so the cached
    /// controller stays valid.
    /// </summary>
    public async Task GroupAllSpeakersAsync(CancellationToken ct = default)
    {
        if (_controller is null || _zones.Count == 0)
            return;

        var coordinatorUuid = _controller.CoordinatorUuid;
        foreach (var zone in _zones)
        {
            if (string.Equals(zone.Uuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                await _soap.InvokeAsync(
                    zone.IpAddress, SonosService.AvTransport, "SetAVTransportURI",
                    [
                        new("InstanceID", "0"),
                        new("CurrentURI", $"x-rincon:{coordinatorUuid}"),
                        new("CurrentURIMetaData", ""),
                    ], ct).ConfigureAwait(false);
            }
            catch
            {
                // One speaker failing to join shouldn't abort the whole-house grouping.
            }
        }
    }

    private void RebuildGroups()
    {
        var totalVisible = _zones.Count;

        Groups = _zones
            .GroupBy(z => z.CoordinatorUuid, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var coord = g.FirstOrDefault(z => z.IsCoordinator) ?? g.First();
                var count = g.Count();
                var name = count == totalVisible && totalVisible > 1
                    ? "All Speakers"
                    : count > 1
                        ? $"{coord.RoomName} + {count - 1}"
                        : coord.RoomName;
                return new SonosGroup(name, coord.RoomName, coord.CoordinatorUuid, coord.CoordinatorIpAddress, count);
            })
            .OrderByDescending(g => g.MemberCount)
            .ThenBy(g => g.CoordinatorRoom, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RebuildController()
    {
        var group =
            Groups.FirstOrDefault(g => string.Equals(g.CoordinatorRoom, ActiveRoom, StringComparison.OrdinalIgnoreCase))
            ?? Groups.FirstOrDefault(g => ContainsRoom(g, ActiveRoom));

        if (group is not null)
        {
            _controller = new SonosController(group.CoordinatorIp, group.CoordinatorUuid, _soap);
            return;
        }

        var zone = _zones.FirstOrDefault(z => string.Equals(z.RoomName, ActiveRoom, StringComparison.OrdinalIgnoreCase));
        _controller = zone is null ? null : SonosController.ForZone(zone, _soap);
    }

    private bool ContainsRoom(SonosGroup group, string? room) =>
        room is not null && _zones.Any(z =>
            string.Equals(z.CoordinatorUuid, group.CoordinatorUuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(z.RoomName, room, StringComparison.OrdinalIgnoreCase));
}
