using HotSonos.App.Infrastructure;
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

/// <summary>A single speaker's current volume/mute, for the per-speaker settings list.</summary>
public sealed record SpeakerVolume(
    string RoomName,
    string IpAddress,
    int Volume,
    bool Muted,
    bool Reachable = true);

/// <summary>
/// App-facing wrapper over the Core UPnP client. Holds the discovered topology
/// as groups and turns <see cref="HotsonosAction"/> into Sonos commands. Cheap
/// to call repeatedly; discovery is cached until refreshed.
/// </summary>
public sealed class SonosManager
{
    private readonly SonosSoapClient _soap = new();
    private readonly SonosDiscovery _discovery;
    private readonly SonosEventSubscriber _events = new();

    private IReadOnlyList<SonosZone> _zones = [];
    private SonosController? _controller;

    private IReadOnlyList<string> _offline = [];
    private bool _topologySeen;

    /// <summary>Raised when the active coordinator pushes a now-playing change.</summary>
    public event Action<NowPlaying>? NowPlayingChanged;

    /// <summary>Raised when the speaker topology changes (regroup / drop / return).</summary>
    public event Action? TopologyChanged;

    /// <summary>Raised when a speaker drops off (false) or comes back (true): (roomName, isOnline).</summary>
    public event Action<string, bool>? SpeakerAvailabilityChanged;

    /// <summary>Rooms currently reported as vanished/offline by Sonos.</summary>
    public IReadOnlyList<string> OfflineSpeakers => _offline;

    public SonosManager()
    {
        _discovery = new SonosDiscovery(_soap);
        _events.NowPlayingChanged += np => NowPlayingChanged?.Invoke(np);
        _events.TopologyChanged += OnTopologyEvent;
    }

    private void OnTopologyEvent(string stateXml)
    {
        try
        {
            var zones = SonosDiscovery.ParseZoneGroupState(stateXml);
            if (zones.Count > 0)
            {
                _zones = zones;
                RebuildGroups();
                RebuildController();
            }

            var vanishedNow = SonosDiscovery.ParseVanishedRooms(stateXml);

            // Skip drop/return balloons for the first snapshot — speakers already
            // offline at startup populate the indicator without a "just dropped" alert.
            if (_topologySeen)
            {
                var previous = new HashSet<string>(_offline, StringComparer.OrdinalIgnoreCase);
                var current = new HashSet<string>(vanishedNow, StringComparer.OrdinalIgnoreCase);
                foreach (var dropped in current.Where(r => !previous.Contains(r)))
                    SpeakerAvailabilityChanged?.Invoke(dropped, false);
                foreach (var returned in previous.Where(r => !current.Contains(r)))
                    SpeakerAvailabilityChanged?.Invoke(returned, true);
            }

            _offline = vanishedNow;
            _topologySeen = true;
            TopologyChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // A malformed topology push shouldn't disrupt anything.
            AppLog.Warn("Topology event parse failed", ex);
        }
    }

    public ValueTask DisposeEventsAsync() => _events.DisposeAsync();

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
        // Fresh start re-discovers first (topology may have drifted, e.g. overnight),
        // so it must run before the "is a room selected" guard.
        if (action == HotsonosAction.FreshStart)
        {
            await RefreshAsync(ActiveRoom, ct).ConfigureAwait(false);
            if (_controller is null)
                throw new InvalidOperationException("No Sonos speakers found. Check the speakers are powered on and on the network.");
            await GroupAllSpeakersAsync(ct).ConfigureAwait(false);
            await _controller.ShuffleMusicLibraryAsync(ct).ConfigureAwait(false);
            return "🔄 Fresh start: re-synced + shuffling all speakers";
        }

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
            case HotsonosAction.VolumeUp:
                return $"🔊 Volume {await ChangeVolumeAsync(settings.VolumeStep, ct).ConfigureAwait(false)}%";
            case HotsonosAction.VolumeDown:
                return $"🔊 Volume {await ChangeVolumeAsync(-settings.VolumeStep, ct).ConfigureAwait(false)}%";
            case HotsonosAction.Mute:
                return await ToggleMuteAsync(ct).ConfigureAwait(false) ? "🔇 Muted" : "🔊 Unmuted";
            case HotsonosAction.LevelVolumes:
                var n = await LevelAllVolumesAsync(settings.LevelVolumePercent, ct).ConfigureAwait(false);
                return $"🔉 Set {n} speaker(s) to {settings.LevelVolumePercent}%";
            default:
                return null;
        }
    }

    // ---- Volume (group-wide) ----------------------------------------------
    // Group-volume WRITE actions (SetGroupVolume/SetGroupMute) return 803 on
    // systems with a fixed-volume member, so we nudge each visible member via
    // per-player RenderingControl and read back the group value for display.

    /// <summary>Adjusts every group member's volume by <paramref name="delta"/> and returns the new group volume.</summary>
    private async Task<int> ChangeVolumeAsync(int delta, CancellationToken ct)
    {
        var members = ActiveGroupMemberIps();
        await Task.WhenAll(members.Select(ip => AdjustMemberVolumeAsync(ip, delta, ct))).ConfigureAwait(false);
        return await GetGroupVolumeAsync(ct).ConfigureAwait(false);
    }

    private async Task AdjustMemberVolumeAsync(string ip, int delta, CancellationToken ct)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetRelativeVolume",
                [
                    new("InstanceID", "0"),
                    new("Channel", "Master"),
                    new("Adjustment", delta.ToString()),
                ], ct).ConfigureAwait(false);
        }
        catch
        {
            // Fixed-volume members (Sub/Port/Amp line-out) reject this; ignore them.
        }
    }

    /// <summary>
    /// Sets EVERY visible speaker (across all groups) to the same absolute volume
    /// and unmutes them, so the whole house plays at one level. Returns the count
    /// of speakers that accepted the change (fixed-volume members are not counted).
    /// </summary>
    public async Task<int> LevelAllVolumesAsync(int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        var ips = _zones.Select(z => z.IpAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = await Task.WhenAll(ips.Select(ip => SetMemberVolumeAsync(ip, percent, ct))).ConfigureAwait(false);
        return results.Count(ok => ok);
    }

    /// <returns>True when the speaker accepted the volume (and unmute) change.</returns>
    private async Task<bool> SetMemberVolumeAsync(string ip, int percent, CancellationToken ct)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetVolume",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredVolume", percent.ToString())], ct).ConfigureAwait(false);
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetMute",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredMute", "0")], ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Fixed-volume members (Sub/Port/Amp line-out) reject volume changes; ignore them.
            return false;
        }
    }

    /// <summary>Reads every visible speaker's current volume/mute, for the settings-window list.</summary>
    public async Task<IReadOnlyList<SpeakerVolume>> GetSpeakerVolumesAsync(CancellationToken ct = default) =>
        await Task.WhenAll(_zones
                .DistinctBy(z => z.IpAddress, StringComparer.OrdinalIgnoreCase)
                .OrderBy(z => z.RoomName, StringComparer.OrdinalIgnoreCase)
                .Select(z => GetSpeakerVolumeAsync(z.RoomName, z.IpAddress, ct)))
            .ConfigureAwait(false);

    private async Task<SpeakerVolume> GetSpeakerVolumeAsync(string roomName, string ip, CancellationToken ct)
    {
        try
        {
            var volumeResponse = await _soap.InvokeAsync(ip, SonosService.RenderingControl, "GetVolume",
                [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
            var muteResponse = await _soap.InvokeAsync(ip, SonosService.RenderingControl, "GetMute",
                [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
            var volume = int.TryParse(SonosSoapClient.ReadValue(volumeResponse, "CurrentVolume"), out var v) ? v : 0;
            var muted = SonosSoapClient.ReadValue(muteResponse, "CurrentMute") == "1";
            return new SpeakerVolume(roomName, ip, volume, muted);
        }
        catch
        {
            return new SpeakerVolume(roomName, ip, 0, false, Reachable: false);
        }
    }

    /// <summary>Sets one speaker's absolute volume (leaves its mute state untouched).</summary>
    public async Task SetSpeakerVolumeAsync(string ip, int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetVolume",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredVolume", percent.ToString())], ct).ConfigureAwait(false);
        }
        catch
        {
            // Fixed-volume members (Sub/Port/Amp line-out) reject volume changes; ignore them.
        }
    }

    /// <summary>Mutes/unmutes one speaker.</summary>
    public async Task SetSpeakerMuteAsync(string ip, bool mute, CancellationToken ct = default)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetMute",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredMute", mute ? "1" : "0")], ct).ConfigureAwait(false);
        }
        catch
        {
            // Tolerate members that reject mute.
        }
    }

    /// <summary>Toggles mute across the group; returns the new muted state.</summary>
    private async Task<bool> ToggleMuteAsync(CancellationToken ct)
    {
        var desired = !await GetGroupMuteAsync(ct).ConfigureAwait(false);
        var members = ActiveGroupMemberIps();
        await Task.WhenAll(members.Select(ip => SetMemberMuteAsync(ip, desired, ct))).ConfigureAwait(false);
        return desired;
    }

    private async Task SetMemberMuteAsync(string ip, bool mute, CancellationToken ct)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetMute",
                [
                    new("InstanceID", "0"),
                    new("Channel", "Master"),
                    new("DesiredMute", mute ? "1" : "0"),
                ], ct).ConfigureAwait(false);
        }
        catch
        {
            // Tolerate members that reject mute.
        }
    }

    private async Task<int> GetGroupVolumeAsync(CancellationToken ct)
    {
        try
        {
            var r = await _soap.InvokeAsync(_controller!.CoordinatorIp, SonosService.GroupRenderingControl,
                "GetGroupVolume", [new("InstanceID", "0")], ct).ConfigureAwait(false);
            return int.TryParse(SonosSoapClient.ReadValue(r, "CurrentVolume"), out var v) ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<bool> GetGroupMuteAsync(CancellationToken ct)
    {
        // Read the coordinator's per-player mute (RenderingControl), not the group
        // mute flag — we set mute per-player (SetGroupMute 803s on this system), so
        // the group flag never changes and would make the toggle one-way.
        try
        {
            var r = await _soap.InvokeAsync(_controller!.CoordinatorIp, SonosService.RenderingControl,
                "GetMute", [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
            return SonosSoapClient.ReadValue(r, "CurrentMute") == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>IPs of the visible members in the active group.</summary>
    private IReadOnlyList<string> ActiveGroupMemberIps()
    {
        if (_controller is null)
            return [];
        return _zones
            .Where(z => string.Equals(z.CoordinatorUuid, _controller.CoordinatorUuid, StringComparison.OrdinalIgnoreCase))
            .Select(z => z.IpAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    /// <summary>
    /// Nightly maintenance: re-discover, and if NOTHING is playing anywhere,
    /// silently regroup every speaker under one coordinator. With
    /// <paramref name="reshuffle"/>, also starts a fresh library shuffle
    /// afterward (this is the one case where the nightly reset starts
    /// playback — opt-in only). Returns a short status describing what happened.
    /// </summary>
    public async Task<string> NightlyResetAsync(bool reshuffle, CancellationToken ct = default)
    {
        await RefreshAsync(ActiveRoom, ct).ConfigureAwait(false);
        if (_controller is null)
            return "no speakers found";

        if (await IsAnythingPlayingAsync(ct).ConfigureAwait(false))
            return "skipped — music is playing";

        await GroupAllSpeakersAsync(ct).ConfigureAwait(false);

        if (!reshuffle)
            return "regrouped all speakers";

        await _controller.ShuffleMusicLibraryAsync(ct).ConfigureAwait(false);
        return "regrouped + reshuffled all speakers";
    }

    /// <summary>True if any group coordinator is currently playing or mid-transition.</summary>
    private async Task<bool> IsAnythingPlayingAsync(CancellationToken ct)
    {
        foreach (var group in Groups)
        {
            try
            {
                var controller = new SonosController(group.CoordinatorIp, group.CoordinatorUuid, _soap);
                var state = await controller.GetTransportStateAsync(ct).ConfigureAwait(false);
                if (state is SonosTransportState.Playing or SonosTransportState.Transitioning)
                    return true;
            }
            catch
            {
                // If we can't read a coordinator, err on the safe side and treat as "not playing"
                // only for that one; a real playing group would normally answer.
            }
        }
        return false;
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
            SubscribeToActiveCoordinator();
            return;
        }

        var zone = _zones.FirstOrDefault(z => string.Equals(z.RoomName, ActiveRoom, StringComparison.OrdinalIgnoreCase));
        _controller = zone is null ? null : SonosController.ForZone(zone, _soap);
        SubscribeToActiveCoordinator();
    }

    private void SubscribeToActiveCoordinator()
    {
        if (_controller is not null)
            _ = _events.SubscribeAsync(_controller.CoordinatorIp);
    }

    private bool ContainsRoom(SonosGroup group, string? room) =>
        room is not null && _zones.Any(z =>
            string.Equals(z.CoordinatorUuid, group.CoordinatorUuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(z.RoomName, room, StringComparison.OrdinalIgnoreCase));
}
