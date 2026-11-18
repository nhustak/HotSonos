using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>
/// One household, driven from the phone with no PC in the path.
/// Discovery, transport, favorites, and library playback stay on <see cref="SonosDiscovery"/>
/// and <see cref="SonosController"/>. This type adds the volume and grouping calls that
/// the tray app keeps inside its Windows-only manager.
/// </summary>
public sealed class SonosDirect
{
    readonly SonosSoapClient _soap;
    readonly SonosDiscovery _discovery;

    public SonosDirect(SonosSoapClient? soap = null)
    {
        _soap = soap ?? new SonosSoapClient();
        _discovery = new SonosDiscovery(_soap);
    }

    public IReadOnlyList<SonosZone> Zones { get; private set; } = [];

    public string? ActiveRoom { get; private set; }

    public NowPlaying? LastNow { get; private set; }

    public SonosZone? ActiveZone =>
        Zones.FirstOrDefault(z => string.Equals(z.RoomName, ActiveRoom, StringComparison.OrdinalIgnoreCase))
        ?? Zones.FirstOrDefault(z => z.IsCoordinator)
        ?? Zones.FirstOrDefault();

    public SonosController? Controller =>
        ActiveZone is { } zone ? SonosController.ForZone(zone, _soap) : null;

    public void SelectRoom(string room)
    {
        if (Zones.Any(z => string.Equals(z.RoomName, room, StringComparison.OrdinalIgnoreCase)))
            ActiveRoom = room;
    }

    public async Task DiscoverAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var zones = await _discovery.DiscoverZonesAsync(timeout ?? TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        Zones = zones;
        if (ActiveRoom is null || zones.All(z => !string.Equals(z.RoomName, ActiveRoom, StringComparison.OrdinalIgnoreCase)))
            ActiveRoom = zones.FirstOrDefault(z => z.IsCoordinator)?.RoomName ?? zones.FirstOrDefault()?.RoomName;
    }

    public async Task<SonosHouseSnapshot> ReadSnapshotAsync(CancellationToken ct = default)
    {
        var zone = ActiveZone;
        NowPlaying? now = null;
        int? volume = null;
        bool? muted = null;
        if (zone is not null && Controller is { } controller)
        {
            try
            {
                now = await controller.GetNowPlayingSnapshotAsync(ct).ConfigureAwait(false);
                LastNow = now;
            }
            catch (SonosSoapException)
            {
                now = LastNow;
            }

            var speaker = await ReadVolumeAsync(zone.CoordinatorIpAddress, zone.RoomName, ct).ConfigureAwait(false);
            if (speaker.Reachable)
            {
                volume = speaker.Volume;
                muted = speaker.Muted;
            }
        }

        var names = zone is null
            ? ""
            : string.Join(" + ", Zones
                .Where(z => string.Equals(z.CoordinatorUuid, zone.CoordinatorUuid, StringComparison.OrdinalIgnoreCase))
                .Select(z => z.RoomName));
        return new SonosHouseSnapshot(zone?.RoomName, string.IsNullOrWhiteSpace(names) ? zone?.RoomName : names, Zones, now, volume, muted);
    }

    public async Task<IReadOnlyList<SonosSpeakerVolume>> ReadVolumesAsync(CancellationToken ct = default)
    {
        var rooms = Zones
            .GroupBy(z => z.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var volumes = await Task.WhenAll(rooms.Select(z => ReadVolumeAsync(z.IpAddress, z.RoomName, ct))).ConfigureAwait(false);
        return volumes;
    }

    public Task SetVolumeAsync(string ip, int percent, CancellationToken ct = default) =>
        _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetVolume",
            [
                new("InstanceID", "0"),
                new("Channel", "Master"),
                new("DesiredVolume", Math.Clamp(percent, 0, 100).ToString()),
            ], ct);

    public async Task SetHouseVolumeAsync(int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        var ips = Zones.Select(z => z.IpAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await Task.WhenAll(ips.Select(ip => SetVolumeSoft(ip, percent, ct))).ConfigureAwait(false);
    }

    public async Task StepHouseVolumeAsync(int delta, CancellationToken ct = default)
    {
        var zone = ActiveZone ?? throw new InvalidOperationException("No speakers yet.");
        var current = await ReadVolumeAsync(zone.CoordinatorIpAddress, zone.RoomName, ct).ConfigureAwait(false);
        var next = Math.Clamp((current.Reachable ? current.Volume : 20) + delta, 0, 100);
        await SetHouseVolumeAsync(next, ct).ConfigureAwait(false);
    }

    public Task SetMuteAsync(string ip, bool mute, CancellationToken ct = default) =>
        _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetMute",
            [
                new("InstanceID", "0"),
                new("Channel", "Master"),
                new("DesiredMute", mute ? "1" : "0"),
            ], ct);

    public async Task<bool> ToggleGroupMuteAsync(CancellationToken ct = default)
    {
        var zone = ActiveZone ?? throw new InvalidOperationException("No speakers yet.");
        var current = await ReadVolumeAsync(zone.CoordinatorIpAddress, zone.RoomName, ct).ConfigureAwait(false);
        var desired = !(current.Reachable && current.Muted);
        var ips = Zones
            .Where(z => string.Equals(z.CoordinatorUuid, zone.CoordinatorUuid, StringComparison.OrdinalIgnoreCase))
            .Select(z => z.IpAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(ips.Select(ip => SetMuteSoft(ip, desired, ct))).ConfigureAwait(false);
        return desired;
    }

    /// <summary>
    /// Make <paramref name="room"/> the leader. <paramref name="wholeHouse"/> joins every other
    /// room; otherwise only the rooms that were already in its group follow it.
    /// Playback is paused first. It is not resumed here.
    /// </summary>
    public async Task SetCoordinatorAsync(string room, bool wholeHouse, CancellationToken ct = default)
    {
        var zone = Find(room);
        var targets = wholeHouse
            ? SonosGrouping.OthersToJoin(Zones, zone)
            : SonosGrouping.FollowersToRejoin(Zones, zone);
        await PauseCoordinatorsAsync(ct).ConfigureAwait(false);
        if (!zone.IsCoordinator)
            await BecomeStandaloneAsync(zone, ct).ConfigureAwait(false);
        foreach (var member in targets)
            await JoinAsync(member, zone.Uuid, ct).ConfigureAwait(false);
        await DiscoverAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        SelectRoom(zone.RoomName);
    }

    /// <summary>
    /// Pull this room out of its group. A follower becomes its own coordinator.
    /// A leader drops every follower into its own group.
    /// </summary>
    public async Task LeaveGroupAsync(string room, CancellationToken ct = default)
    {
        var zone = Find(room);
        if (zone.IsCoordinator)
        {
            var members = Zones.Where(z =>
                !string.Equals(z.Uuid, zone.Uuid, StringComparison.OrdinalIgnoreCase)
                && string.Equals(z.CoordinatorUuid, zone.Uuid, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var member in members)
            {
                try { await BecomeStandaloneAsync(member, ct).ConfigureAwait(false); }
                catch (SonosSoapException) { }
            }
        }
        else
        {
            await BecomeStandaloneAsync(zone, ct).ConfigureAwait(false);
        }

        await DiscoverAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        SelectRoom(zone.RoomName);
    }

    async Task<SonosSpeakerVolume> ReadVolumeAsync(string ip, string room, CancellationToken ct)
    {
        try
        {
            var volumeResponse = await _soap.InvokeAsync(ip, SonosService.RenderingControl, "GetVolume",
                [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
            var muteResponse = await _soap.InvokeAsync(ip, SonosService.RenderingControl, "GetMute",
                [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
            var volume = int.TryParse(SonosSoapClient.ReadValue(volumeResponse, "CurrentVolume"), out var v) ? v : 0;
            var muted = SonosSoapClient.ReadValue(muteResponse, "CurrentMute") == "1";
            return new SonosSpeakerVolume(room, ip, volume, muted, true);
        }
        catch (Exception ex) when (ex is SonosSoapException or HttpRequestException or TaskCanceledException)
        {
            return new SonosSpeakerVolume(room, ip, 0, false, false);
        }
    }

    async Task SetVolumeSoft(string ip, int percent, CancellationToken ct)
    {
        try { await SetVolumeAsync(ip, percent, ct).ConfigureAwait(false); }
        catch (SonosSoapException) { }
    }

    async Task SetMuteSoft(string ip, bool mute, CancellationToken ct)
    {
        try { await SetMuteAsync(ip, mute, ct).ConfigureAwait(false); }
        catch (SonosSoapException) { }
    }

    async Task PauseCoordinatorsAsync(CancellationToken ct)
    {
        var leaders = Zones.Where(z => z.IsCoordinator).GroupBy(z => z.IpAddress, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
        foreach (var leader in leaders)
        {
            try
            {
                var controller = SonosController.ForZone(leader, _soap);
                var state = await controller.GetTransportStateAsync(ct).ConfigureAwait(false);
                if (state == SonosTransportState.Playing)
                    await controller.PauseAsync(ct).ConfigureAwait(false);
            }
            catch (SonosSoapException) { }
        }
    }

    Task BecomeStandaloneAsync(SonosZone zone, CancellationToken ct) =>
        _soap.InvokeAsync(zone.IpAddress, SonosService.AvTransport, "BecomeCoordinatorOfStandaloneGroup",
            [new("InstanceID", "0")], ct);

    Task JoinAsync(SonosZone member, string coordinatorUuid, CancellationToken ct) =>
        _soap.InvokeAsync(member.IpAddress, SonosService.AvTransport, "SetAVTransportURI",
            [
                new("InstanceID", "0"),
                new("CurrentURI", SonosGrouping.RinconUri(coordinatorUuid)),
                new("CurrentURIMetaData", ""),
            ], ct);

    SonosZone Find(string room)
    {
        var zone = Zones.FirstOrDefault(z => string.Equals(z.RoomName, room, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
            throw new InvalidOperationException($"Room '{room}' is not in the last discovery.");
        return zone;
    }
}
