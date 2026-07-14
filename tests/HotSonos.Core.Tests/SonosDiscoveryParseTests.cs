namespace HotSonos.Core.Tests;

public class SonosDiscoveryParseTests
{
    private const string ZoneGroupState = """
        <ZoneGroupState>
          <ZoneGroups>
            <ZoneGroup Coordinator="RINCON_AAA01400" ID="RINCON_AAA01400:1">
              <ZoneGroupMember UUID="RINCON_AAA01400" ZoneName="Living Room"
                Location="http://192.168.1.10:1400/xml/device_description.xml"/>
              <ZoneGroupMember UUID="RINCON_BBB01400" ZoneName="Kitchen"
                Location="http://192.168.1.11:1400/xml/device_description.xml"/>
              <ZoneGroupMember UUID="RINCON_SUB01400" ZoneName="Living Room Sub"
                Location="http://192.168.1.12:1400/xml/device_description.xml" Invisible="1"/>
            </ZoneGroup>
            <ZoneGroup Coordinator="RINCON_CCC01400" ID="RINCON_CCC01400:2">
              <ZoneGroupMember UUID="RINCON_CCC01400" ZoneName="Office"
                Location="http://192.168.1.20:1400/xml/device_description.xml"/>
            </ZoneGroup>
          </ZoneGroups>
          <VanishedDevices>
            <Device ZoneName="Patio" UUID="RINCON_DDD01400"/>
            <Device ZoneName="Patio" UUID="RINCON_DDD01400"/>
          </VanishedDevices>
        </ZoneGroupState>
        """;

    [Fact]
    public void ParseZoneGroupState_skips_invisible_and_sets_coordinator()
    {
        var zones = SonosDiscovery.ParseZoneGroupState(ZoneGroupState);

        Assert.Equal(3, zones.Count); // Sub is Invisible="1"
        Assert.DoesNotContain(zones, z => z.RoomName.Contains("Sub", StringComparison.OrdinalIgnoreCase));

        var living = zones.Single(z => z.RoomName == "Living Room");
        Assert.True(living.IsCoordinator);
        Assert.Equal("192.168.1.10", living.CoordinatorIpAddress);
        Assert.Equal("RINCON_AAA01400", living.CoordinatorUuid);

        var kitchen = zones.Single(z => z.RoomName == "Kitchen");
        Assert.False(kitchen.IsCoordinator);
        Assert.Equal("192.168.1.10", kitchen.CoordinatorIpAddress);
        Assert.Equal("192.168.1.11", kitchen.IpAddress);

        var office = zones.Single(z => z.RoomName == "Office");
        Assert.True(office.IsCoordinator);
        Assert.Equal("192.168.1.20", office.CoordinatorIpAddress);
    }

    [Fact]
    public void ParseVanishedRooms_dedupes_case_insensitively()
    {
        var vanished = SonosDiscovery.ParseVanishedRooms(ZoneGroupState);

        Assert.Equal(["Patio"], vanished);
    }
}
