using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>
/// Snapshot and apply speaker bond layouts (stereo pairs and Subs) via DeviceProperties
/// CreateStereoPair / SeparateStereoPair. Verified on firmware 98.0-82170 with a
/// Gen 3 Sub, Era 100 pair, and a One SL.
/// </summary>
public sealed class SonosBonding
{
    public const string CreateStereoPair = "CreateStereoPair";
    public const string SeparateStereoPair = "SeparateStereoPair";

    public static readonly TimeSpan FreeSubPause = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<string, string, string, CancellationToken, Task> _invoke;
    private readonly Func<CancellationToken, Task<SonosTopologySnapshot>> _topology;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;

    public SonosBonding(
        Func<string, string, string, CancellationToken, Task> invoke,
        Func<CancellationToken, Task<SonosTopologySnapshot>> topology,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? utcNow = null)
    {
        _invoke = invoke;
        _topology = topology;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public static bool IsSubFree(SonosTopologySnapshot snap, string subUuid)
    {
        var member = Find(snap, subUuid);
        if (member is null)
            return false;
        // A free Sub is a ZoneGroupMember, not a Satellite, and has no ChannelMapSet.
        // It can still be Invisible=1 and gets its own group.
        return string.IsNullOrWhiteSpace(member.ChannelMapSet);
    }

    public static SpeakerLayout Snapshot(SonosTopologySnapshot snap, string name, IEnumerable<string> affectedRooms)
    {
        var wanted = new HashSet<string>(
            affectedRooms.Where(r => !string.IsNullOrWhiteSpace(r)),
            StringComparer.OrdinalIgnoreCase);
        var layout = new SpeakerLayout { Name = name.Trim() };
        foreach (var roomName in wanted)
        {
            var primary = snap.Members.FirstOrDefault(m =>
                !m.Invisible && string.Equals(m.RoomName, roomName, StringComparison.OrdinalIgnoreCase));
            if (primary is null)
                continue;
            var map = primary.ChannelMapSet ?? "";
            layout.Rooms.Add(new LayoutRoom
            {
                RoomName = primary.RoomName,
                PrimaryUuid = primary.Uuid,
                RightUuid = ChannelMaps.RightUuid(map),
                SubUuid = ChannelMaps.SubUuid(map),
                ChannelMapSet = map,
            });
        }

        return layout;
    }

    /// <summary>
    /// Rewrite a snapshot so the household Sub sits on <paramref name="targetRoomName"/>
    /// without moving hardware. Other affected rooms keep their maps minus the Sub.
    /// Used to build "Sub in Kitchen" while the Sub is still in Office.
    /// </summary>
    public static SpeakerLayout RelocateSub(SpeakerLayout source, string targetRoomName, string? subUuid = null)
    {
        var sub = subUuid
                  ?? source.Rooms.Select(r => r.SubUuid).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))
                  ?? ChannelMaps.SubUuid(source.Rooms.Select(r => r.ChannelMapSet).FirstOrDefault(ChannelMaps.HasSub));
        if (string.IsNullOrWhiteSpace(sub))
            throw new InvalidOperationException("No Sub in this layout to relocate.");

        var target = source.Rooms.FirstOrDefault(r =>
            string.Equals(r.RoomName, targetRoomName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            throw new InvalidOperationException($"{targetRoomName} is not in this layout.");

        var copy = Clone(source);
        foreach (var room in copy.Rooms)
        {
            if (string.Equals(room.RoomName, target.RoomName, StringComparison.OrdinalIgnoreCase))
            {
                room.ChannelMapSet = ChannelMaps.WithSub(room.ChannelMapSet, room.PrimaryUuid, room.RightUuid, sub);
                room.SubUuid = sub;
            }
            else
            {
                room.ChannelMapSet = ChannelMaps.WithoutSub(room.ChannelMapSet);
                room.SubUuid = null;
            }
        }

        return copy;
    }

    public static bool Matches(SpeakerLayout layout, SonosTopologySnapshot snap)
    {
        if (layout.Rooms.Count == 0)
            return false;
        foreach (var room in layout.Rooms)
        {
            var member = FindRoomPrimary(snap, room);
            if (member is null)
                return false;
            if (!ChannelMaps.EqualsMap(member.ChannelMapSet, room.ChannelMapSet))
                return false;
        }

        return true;
    }

    public static string? SubHostRoom(SpeakerLayout layout) =>
        layout.Rooms.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SubUuid) || ChannelMaps.HasSub(r.ChannelMapSet))
            ?.RoomName;

    public static SpeakerLayout? NextLayout(IReadOnlyList<SpeakerLayout> layouts, SonosTopologySnapshot snap)
    {
        if (layouts.Count == 0)
            return null;
        for (var i = 0; i < layouts.Count; i++)
        {
            if (Matches(layouts[i], snap))
                return layouts[(i + 1) % layouts.Count];
        }

        return layouts[0];
    }

    public static string SuccessMessage(SpeakerLayout layout, bool noOp)
    {
        if (noOp)
            return "Already this layout";
        var host = SubHostRoom(layout);
        return string.IsNullOrWhiteSpace(host) ? $"Applied {layout.Name}" : $"Sub moved to {host}";
    }

    public async Task<BondApplyResult> ApplyAsync(SpeakerLayout layout, CancellationToken ct = default)
    {
        if (layout.Rooms.Count == 0)
            return new BondApplyResult(false, "This layout has no rooms.", FailedStep: "Validate");

        var snap = await _topology(ct).ConfigureAwait(false);
        if (Matches(layout, snap))
            return new BondApplyResult(true, SuccessMessage(layout, noOp: true), NoOp: true);

        var blocked = SubOnUncheckedRoom(layout, snap);
        if (blocked is not null)
            return blocked;

        try
        {
            snap = await SeparateMismatchedAsync(layout, snap, ct).ConfigureAwait(false);
            snap = await RebuildPairsAsync(layout, snap, ct).ConfigureAwait(false);
            snap = await WaitForNeededSubsFreeAsync(layout, snap, ct).ConfigureAwait(false);
            await _delay(FreeSubPause, ct).ConfigureAwait(false);
            snap = await BondTargetsAsync(layout, snap, ct).ConfigureAwait(false);
            snap = await WaitUntilAsync(
                "Wait for layout to match",
                s => Matches(layout, s),
                snap,
                ct).ConfigureAwait(false);

            if (!Matches(layout, snap))
                return new BondApplyResult(false, "Layout did not match after bonding.", FailedStep: "Confirm maps");

            return new BondApplyResult(true, SuccessMessage(layout, noOp: false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            return new BondApplyResult(false, ex.Message, FailedStep: "Wait");
        }
        catch (SonosSoapException ex)
        {
            var code = ex.ErrorCode is int c ? $" UPnP {c}." : "";
            return new BondApplyResult(false, $"{ex.Message}", FailedStep: "SOAP" + code);
        }
        catch (Exception ex)
        {
            return new BondApplyResult(false, ex.Message, FailedStep: "Apply");
        }
    }

    private static BondApplyResult? SubOnUncheckedRoom(SpeakerLayout layout, SonosTopologySnapshot snap)
    {
        var wantedSubs = layout.Rooms
            .Select(r => r.SubUuid ?? ChannelMaps.SubUuid(r.ChannelMapSet))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var subUuid in wantedSubs)
        {
            var host = CurrentSubHost(snap, subUuid);
            if (host is null)
                continue;
            var affected = layout.Rooms.Any(r =>
                string.Equals(r.RoomName, host.RoomName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.PrimaryUuid, host.Uuid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.RightUuid, host.Uuid, StringComparison.OrdinalIgnoreCase));
            if (!affected)
            {
                return new BondApplyResult(
                    false,
                    $"Sub is in {host.RoomName}; that room isn't in this layout.",
                    FailedStep: "Validate");
            }
        }

        return null;
    }

    private async Task<SonosTopologySnapshot> SeparateMismatchedAsync(
        SpeakerLayout layout, SonosTopologySnapshot snap, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in layout.Rooms)
        {
            var member = FindRoomPrimary(snap, room);
            if (member is null)
                throw new InvalidOperationException($"Can't find {room.RoomName} on the network.");
            var current = member.ChannelMapSet ?? "";
            if (!NeedsSeparate(current, room.ChannelMapSet))
                continue;
            if (!seen.Add(current))
                continue;
            var ip = member.IpAddress;
            if (string.IsNullOrWhiteSpace(ip))
                throw new InvalidOperationException($"No IP for {room.RoomName}.");
            await InvokeAsync(ip, SeparateStereoPair, current, $"SeparateStereoPair on {room.RoomName}", ct)
                .ConfigureAwait(false);
        }

        return await _topology(ct).ConfigureAwait(false);
    }

    private async Task<SonosTopologySnapshot> RebuildPairsAsync(
        SpeakerLayout layout, SonosTopologySnapshot snap, CancellationToken ct)
    {
        foreach (var room in layout.Rooms)
        {
            if (string.IsNullOrWhiteSpace(room.RightUuid))
                continue;
            snap = await _topology(ct).ConfigureAwait(false);
            var member = FindRoomPrimary(snap, room);
            if (member is null)
                throw new InvalidOperationException($"Can't find {room.RoomName} on the network.");
            var current = member.ChannelMapSet ?? "";
            var pairMap = ChannelMaps.WithoutSub(room.ChannelMapSet);
            if (string.IsNullOrWhiteSpace(pairMap) || ChannelMaps.EqualsMap(current, pairMap) || ChannelMaps.EqualsMap(current, room.ChannelMapSet))
                continue;
            if (!string.IsNullOrWhiteSpace(current) && ChannelMaps.CanAdditivelyReach(current, room.ChannelMapSet))
                continue;
            if (!string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(ChannelMaps.WithoutSub(current)))
            {
                // Something unexpected is still bonded; don't invent a shrink.
                continue;
            }

            await InvokeAsync(member.IpAddress, CreateStereoPair, pairMap, $"Rebuild pair in {room.RoomName}", ct)
                .ConfigureAwait(false);
            snap = await WaitUntilAsync(
                $"Wait for {room.RoomName} pair",
                s =>
                {
                    var now = FindRoomPrimary(s, room);
                    return now is not null && ChannelMaps.EqualsMap(now.ChannelMapSet, pairMap);
                },
                snap,
                ct).ConfigureAwait(false);
        }

        return snap;
    }

    private async Task<SonosTopologySnapshot> WaitForNeededSubsFreeAsync(
        SpeakerLayout layout, SonosTopologySnapshot snap, CancellationToken ct)
    {
        var subs = layout.Rooms
            .Select(r => r.SubUuid ?? ChannelMaps.SubUuid(r.ChannelMapSet))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (subs.Count == 0)
            return snap;

        return await WaitUntilAsync(
            "Wait for Sub to be free",
            s =>
            {
                foreach (var sub in subs)
                {
                    var host = CurrentSubHost(s, sub);
                    if (host is null)
                        continue;
                    var desiredHost = layout.Rooms.FirstOrDefault(r =>
                        string.Equals(r.SubUuid ?? ChannelMaps.SubUuid(r.ChannelMapSet), sub, StringComparison.OrdinalIgnoreCase));
                    if (desiredHost is null)
                        continue;
                    // Already on the desired room with the desired map: not required to be free.
                    if (ChannelMaps.EqualsMap(host.ChannelMapSet, desiredHost.ChannelMapSet)
                        && string.Equals(host.Uuid, desiredHost.PrimaryUuid, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!IsSubFree(s, sub))
                        return false;
                }

                return true;
            },
            snap,
            ct).ConfigureAwait(false);
    }

    private async Task<SonosTopologySnapshot> BondTargetsAsync(
        SpeakerLayout layout, SonosTopologySnapshot snap, CancellationToken ct)
    {
        foreach (var room in layout.Rooms)
        {
            if (string.IsNullOrWhiteSpace(room.ChannelMapSet))
                continue;
            snap = await _topology(ct).ConfigureAwait(false);
            var member = FindRoomPrimary(snap, room);
            if (member is null)
                throw new InvalidOperationException($"Can't find {room.RoomName} on the network.");
            if (ChannelMaps.EqualsMap(member.ChannelMapSet, room.ChannelMapSet))
                continue;
            await InvokeAsync(
                    member.IpAddress,
                    CreateStereoPair,
                    room.ChannelMapSet,
                    $"CreateStereoPair on {room.RoomName}",
                    ct)
                .ConfigureAwait(false);
        }

        return await _topology(ct).ConfigureAwait(false);
    }

    private async Task InvokeAsync(string ip, string action, string map, string step, CancellationToken ct)
    {
        try
        {
            await _invoke(ip, action, map, ct).ConfigureAwait(false);
        }
        catch (SonosSoapException ex)
        {
            var code = ex.ErrorCode is int c ? $" UPnP {c}" : "";
            throw new InvalidOperationException($"{step} failed{code}: {ex.Message}", ex);
        }
    }

    private async Task<SonosTopologySnapshot> WaitUntilAsync(
        string step,
        Func<SonosTopologySnapshot, bool> predicate,
        SonosTopologySnapshot last,
        CancellationToken ct)
    {
        var deadline = _utcNow() + PollTimeout;
        var snap = last;
        if (predicate(snap))
            return snap;
        while (_utcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await _delay(PollInterval, ct).ConfigureAwait(false);
            snap = await _topology(ct).ConfigureAwait(false);
            if (predicate(snap))
                return snap;
        }

        throw new TimeoutException($"{step} timed out after {PollTimeout.TotalSeconds:0}s.");
    }

    private static bool NeedsSeparate(string? current, string? desired)
    {
        if (ChannelMaps.EqualsMap(current, desired) || string.IsNullOrWhiteSpace(current))
            return false;
        if (ChannelMaps.CanAdditivelyReach(current, desired))
            return false;
        return true;
    }

    internal static SonosTopologyMember? FindRoomPrimary(SonosTopologySnapshot snap, LayoutRoom room)
    {
        var byUuid = Find(snap, room.PrimaryUuid);
        if (byUuid is not null)
        {
            // Prefer the visible primary when the UUID still exists; the map lives on it.
            if (!byUuid.Invisible || string.IsNullOrWhiteSpace(room.RoomName))
                return byUuid;
        }

        return snap.Members.FirstOrDefault(m =>
            !m.Invisible && string.Equals(m.RoomName, room.RoomName, StringComparison.OrdinalIgnoreCase));
    }

    internal static SonosTopologyMember? CurrentSubHost(SonosTopologySnapshot snap, string subUuid)
    {
        var sub = Find(snap, subUuid);
        var map = sub?.ChannelMapSet;
        if (string.IsNullOrWhiteSpace(map))
            return null;
        var primaryUuid = ChannelMaps.PrimaryUuid(map);
        return Find(snap, primaryUuid) ?? snap.Members.FirstOrDefault(m =>
            !m.Invisible && ChannelMaps.EqualsMap(m.ChannelMapSet, map));
    }

    internal static SonosTopologyMember? Find(SonosTopologySnapshot snap, string? uuid) =>
        string.IsNullOrWhiteSpace(uuid)
            ? null
            : snap.Members.FirstOrDefault(m => string.Equals(m.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

    internal static SpeakerLayout Clone(SpeakerLayout source) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            Rooms = source.Rooms.Select(r => new LayoutRoom
            {
                RoomName = r.RoomName,
                PrimaryUuid = r.PrimaryUuid,
                RightUuid = r.RightUuid,
                SubUuid = r.SubUuid,
                ChannelMapSet = r.ChannelMapSet,
            }).ToList(),
        };
}
