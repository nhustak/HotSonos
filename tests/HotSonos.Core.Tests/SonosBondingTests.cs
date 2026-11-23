using HotSonos.Core.Models;

namespace HotSonos.Core.Tests;

public class SonosBondingTests
{
    internal const string OfficeL = "RINCON_804AF29BE1FC01400";
    internal const string OfficeR = "RINCON_804AF29BE66C01400";
    internal const string Sub = "RINCON_F0F6C1EEA61401400";
    internal const string Kitchen = "RINCON_C4387578325001400";

    internal const string OfficePairSub =
        "RINCON_804AF29BE1FC01400:LF,LF;RINCON_804AF29BE66C01400:RF,RF;RINCON_F0F6C1EEA61401400:SW";
    internal const string OfficePair =
        "RINCON_804AF29BE1FC01400:LF,LF;RINCON_804AF29BE66C01400:RF,RF";
    internal const string KitchenSub =
        "RINCON_C4387578325001400:LF,RF;RINCON_F0F6C1EEA61401400:SW";

    [Fact]
    public void Parse_and_rewrite_maps_match_live_formats()
    {
        Assert.Equal(Sub, ChannelMaps.SubUuid(OfficePairSub));
        Assert.Equal(OfficeR, ChannelMaps.RightUuid(OfficePairSub));
        Assert.Equal(OfficeL, ChannelMaps.PrimaryUuid(OfficePairSub));
        Assert.Equal(OfficePair, ChannelMaps.WithoutSub(OfficePairSub));
        Assert.Equal(OfficePairSub, ChannelMaps.WithSub(OfficePair, OfficeL, OfficeR, Sub));
        Assert.Equal(KitchenSub, ChannelMaps.WithSub("", Kitchen, null, Sub));
        Assert.True(ChannelMaps.CanAdditivelyReach(OfficePair, OfficePairSub));
        Assert.False(ChannelMaps.CanAdditivelyReach(OfficePairSub, OfficePair));
        Assert.True(ChannelMaps.EqualsMap(OfficePairSub, OfficePairSub));
        Assert.True(ChannelMaps.EqualsMap("", null));
        Assert.False(ChannelMaps.EqualsMap(OfficePair, OfficePairSub));
    }

    [Fact]
    public void Snapshot_stores_exact_maps_only_for_checked_rooms()
    {
        var snap = Parse(OfficeBondedXml());
        var layout = SonosBonding.Snapshot(snap, "Sub in Office", ["Office", "Kitchen"]);

        Assert.Equal("Sub in Office", layout.Name);
        Assert.Equal(2, layout.Rooms.Count);
        var office = layout.Rooms.Single(r => r.RoomName == "Office");
        Assert.Equal(OfficeL, office.PrimaryUuid);
        Assert.Equal(OfficeR, office.RightUuid);
        Assert.Equal(Sub, office.SubUuid);
        Assert.Equal(OfficePairSub, office.ChannelMapSet);
        var kitchen = layout.Rooms.Single(r => r.RoomName == "Kitchen");
        Assert.Equal(Kitchen, kitchen.PrimaryUuid);
        Assert.Null(kitchen.RightUuid);
        Assert.Null(kitchen.SubUuid);
        Assert.Equal("", kitchen.ChannelMapSet);
        Assert.True(SonosBonding.Matches(layout, snap));
        Assert.Equal("Office", SonosBonding.SubHostRoom(layout));
    }

    [Fact]
    public void RelocateSub_builds_Kitchen_layout_without_moving_hardware()
    {
        var snap = Parse(OfficeBondedXml());
        var source = SonosBonding.Snapshot(snap, "Sub in Office", ["Office", "Kitchen"]);
        var kitchen = SonosBonding.RelocateSub(source, "Kitchen");
        kitchen.Name = "Sub in Kitchen";

        var office = kitchen.Rooms.Single(r => r.RoomName == "Office");
        Assert.Equal(OfficePair, office.ChannelMapSet);
        Assert.Null(office.SubUuid);
        var kit = kitchen.Rooms.Single(r => r.RoomName == "Kitchen");
        Assert.Equal(KitchenSub, kit.ChannelMapSet);
        Assert.Equal(Sub, kit.SubUuid);
        Assert.False(SonosBonding.Matches(kitchen, snap));
        Assert.Equal("Kitchen", SonosBonding.SubHostRoom(kitchen));
    }

    [Fact]
    public async Task Apply_is_noop_when_maps_already_match()
    {
        var snap = Parse(OfficeBondedXml());
        var layout = SonosBonding.Snapshot(snap, "Sub in Office", ["Office", "Kitchen"]);
        var calls = new List<(string Ip, string Action, string Map)>();
        var bonding = NewBonding(_ => snap, calls);

        var result = await bonding.ApplyAsync(layout);

        Assert.True(result.Ok);
        Assert.True(result.NoOp);
        Assert.Equal("Already this layout", result.Message);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Apply_Office_to_Kitchen_separates_rebuilds_pair_then_bonds_once()
    {
        var state = new LayoutState(LayoutState.Phase.OfficeBonded);
        var calls = new List<(string Ip, string Action, string Map)>();
        var delays = new List<TimeSpan>();
        var now = DateTime.UtcNow;
        var bonding = NewBonding(_ => state.Snapshot(), calls, (t, _) =>
        {
            delays.Add(t);
            now += t;
            return Task.CompletedTask;
        }, () => now, state);

        var layout = KitchenLayout();
        var result = await bonding.ApplyAsync(layout);

        Assert.True(result.Ok, result.Message);
        Assert.False(result.NoOp);
        Assert.Equal("Sub moved to Kitchen", result.Message);
        Assert.Equal(
        [
            ("192.168.1.114", SonosBonding.SeparateStereoPair, OfficePairSub),
            ("192.168.1.114", SonosBonding.CreateStereoPair, OfficePair),
            ("192.168.1.117", SonosBonding.CreateStereoPair, KitchenSub),
        ], calls);
        Assert.Contains(SonosBonding.FreeSubPause, delays);
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task Apply_Kitchen_to_Office_unbonds_then_additively_rebonds_Office()
    {
        var state = new LayoutState(LayoutState.Phase.KitchenBonded);
        var calls = new List<(string Ip, string Action, string Map)>();
        var now = DateTime.UtcNow;
        var bonding = NewBonding(_ => state.Snapshot(), calls, (t, _) =>
        {
            now += t;
            return Task.CompletedTask;
        }, () => now, state);

        var result = await bonding.ApplyAsync(OfficeLayout());

        Assert.True(result.Ok, result.Message);
        Assert.Equal("Sub moved to Office", result.Message);
        Assert.Equal(
        [
            ("192.168.1.117", SonosBonding.SeparateStereoPair, KitchenSub),
            ("192.168.1.114", SonosBonding.CreateStereoPair, OfficePairSub),
        ], calls);
    }

    [Fact]
    public async Task Apply_fails_when_Sub_is_on_an_unchecked_room()
    {
        var snap = Parse(OfficeBondedXml());
        var layout = new SpeakerLayout
        {
            Name = "Sub in Kitchen only",
            Rooms =
            [
                new LayoutRoom
                {
                    RoomName = "Kitchen",
                    PrimaryUuid = Kitchen,
                    SubUuid = Sub,
                    ChannelMapSet = KitchenSub,
                },
            ],
        };
        var calls = new List<(string Ip, string Action, string Map)>();
        var bonding = NewBonding(_ => snap, calls);

        var result = await bonding.ApplyAsync(layout);

        Assert.False(result.Ok);
        Assert.Equal("Sub is in Office; that room isn't in this layout.", result.Message);
        Assert.Equal("Validate", result.FailedStep);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Apply_reports_UPnP_error_and_does_not_retry()
    {
        var snap = Parse(OfficeBondedXml());
        var layout = KitchenLayout();
        var calls = 0;
        var bonding = new SonosBonding(
            (_, _, _, _) =>
            {
                calls++;
                throw new SonosSoapException("SOAP SeparateStereoPair failed: HTTP 500. UPnP 800. Body: …", 800);
            },
            _ => Task.FromResult(snap));

        var result = await bonding.ApplyAsync(layout);

        Assert.False(result.Ok);
        Assert.Equal(1, calls);
        Assert.Contains("UPnP 800", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NextLayout_cycles_from_the_matching_one()
    {
        var office = OfficeLayout();
        var kitchen = KitchenLayout();
        var snap = Parse(OfficeBondedXml());
        Assert.Same(kitchen, SonosBonding.NextLayout([office, kitchen], snap));
        Assert.Same(office, SonosBonding.NextLayout([office, kitchen], Parse(KitchenBondedXml())));
        Assert.Same(office, SonosBonding.NextLayout([office, kitchen], Parse(SplitXml())));
    }

    internal static SpeakerLayout OfficeLayout()
    {
        var layout = SonosBonding.Snapshot(Parse(OfficeBondedXml()), "Sub in Office", ["Office", "Kitchen"]);
        return layout;
    }

    internal static SpeakerLayout KitchenLayout()
    {
        var layout = SonosBonding.RelocateSub(OfficeLayout(), "Kitchen");
        layout.Name = "Sub in Kitchen";
        return layout;
    }

    internal static SonosTopologySnapshot Parse(string xml) => SonosDiscovery.ParseTopologySnapshot(xml);

    static SonosBonding NewBonding(
        Func<CancellationToken, SonosTopologySnapshot> topology,
        List<(string Ip, string Action, string Map)>? calls = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? utcNow = null,
        LayoutState? state = null) =>
        new(
            (ip, action, map, _) =>
            {
                calls?.Add((ip, action, map));
                state?.Apply(action, map);
                return Task.CompletedTask;
            },
            ct => Task.FromResult(topology(ct)),
            delay ?? ((_, _) => Task.CompletedTask),
            utcNow);

    internal static string OfficeBondedXml() => HouseXml(
        officeMap: OfficePairSub,
        officeRightInvisible: true,
        subWith: "office",
        kitchenMap: null);

    internal static string KitchenBondedXml() => HouseXml(
        officeMap: OfficePair,
        officeRightInvisible: true,
        subWith: "kitchen",
        kitchenMap: KitchenSub);

    internal static string SplitXml() => HouseXml(
        officeMap: null,
        officeRightInvisible: false,
        subWith: "free",
        kitchenMap: null);

    internal static string OfficePairFreeSubXml() => HouseXml(
        officeMap: OfficePair,
        officeRightInvisible: true,
        subWith: "free",
        kitchenMap: null);

    internal static string HouseXml(string? officeMap, bool officeRightInvisible, string subWith, string? kitchenMap)
    {
        var officeMapAttr = officeMap is null ? "" : $" ChannelMapSet=\"{officeMap}\"";
        var kitchenMapAttr = kitchenMap is null ? "" : $" ChannelMapSet=\"{kitchenMap}\"";
        var rightInv = officeRightInvisible ? " Invisible=\"1\"" : "";
        var (subGroup, subInv, subMap) = subWith switch
        {
            "office" => ("office", " Invisible=\"1\"", officeMapAttr),
            "kitchen" => ("kitchen", " Invisible=\"1\"", kitchenMapAttr),
            _ => ("free", " Invisible=\"1\"", ""),
        };

        var officeMembers = $"""
              <ZoneGroupMember UUID="{OfficeL}" ZoneName="Office"
                Location="http://192.168.1.114:1400/xml/device_description.xml"{officeMapAttr}/>
              <ZoneGroupMember UUID="{OfficeR}" ZoneName="Office"
                Location="http://192.168.1.139:1400/xml/device_description.xml"{rightInv}/>
            """;
        var kitchenMember = $"""
              <ZoneGroupMember UUID="{Kitchen}" ZoneName="Kitchen"
                Location="http://192.168.1.117:1400/xml/device_description.xml"{kitchenMapAttr}/>
            """;
        var subMember = $"""
              <ZoneGroupMember UUID="{Sub}" ZoneName="Office"
                Location="http://192.168.1.156:1400/xml/device_description.xml"{subInv}{subMap}/>
            """;

        var officeGroupMembers = officeMembers;
        var kitchenGroupMembers = kitchenMember;
        if (subGroup == "office")
            officeGroupMembers += "\n" + subMember;
        else if (subGroup == "kitchen")
            kitchenGroupMembers += "\n" + subMember;

        var groups = $"""
            <ZoneGroup Coordinator="{OfficeL}" ID="{OfficeL}:1">
            {officeGroupMembers}
            {kitchenGroupMembers}
            </ZoneGroup>
            """;
        if (subGroup == "free")
        {
            groups += $"""
                <ZoneGroup Coordinator="{Sub}" ID="{Sub}:1">
                {subMember}
                </ZoneGroup>
                """;
        }

        return $"""
            <ZoneGroupState>
              <ZoneGroups>
            {groups}
              </ZoneGroups>
            </ZoneGroupState>
            """;
    }

    sealed class LayoutState(LayoutState.Phase start)
    {
        public enum Phase { OfficeBonded, Split, OfficePairFreeSub, KitchenBonded }

        public Phase Current { get; private set; } = start;

        public SonosTopologySnapshot Snapshot() => Current switch
        {
            Phase.OfficeBonded => Parse(OfficeBondedXml()),
            Phase.Split => Parse(SplitXml()),
            Phase.OfficePairFreeSub => Parse(OfficePairFreeSubXml()),
            Phase.KitchenBonded => Parse(KitchenBondedXml()),
            _ => Parse(OfficeBondedXml()),
        };

        public void Apply(string action, string map)
        {
            if (action == SonosBonding.SeparateStereoPair && ChannelMaps.EqualsMap(map, OfficePairSub))
                Current = Phase.Split;
            else if (action == SonosBonding.SeparateStereoPair && ChannelMaps.EqualsMap(map, KitchenSub))
                Current = Phase.OfficePairFreeSub;
            else if (action == SonosBonding.CreateStereoPair && ChannelMaps.EqualsMap(map, OfficePair))
                Current = Phase.OfficePairFreeSub;
            else if (action == SonosBonding.CreateStereoPair && ChannelMaps.EqualsMap(map, KitchenSub))
                Current = Phase.KitchenBonded;
            else if (action == SonosBonding.CreateStereoPair && ChannelMaps.EqualsMap(map, OfficePairSub))
                Current = Phase.OfficeBonded;
        }
    }
}
