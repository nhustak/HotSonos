namespace HotSonos.Core.Tests;

public class SonosDiscoveryParseTests
{
    private const string ZoneGroupState = """
        <ZoneGroupState>
          <ZoneGroups>
            <ZoneGroup Coordinator="RINCON_AAA01400" ID="RINCON_AAA01400:1">
              <ZoneGroupMember UUID="RINCON_AAA01400" ZoneName="Living Room"
                Location="http://192.168.1.10:1400/xml/device_description.xml"
                EthLink="1" WifiEnabled="0" ConnectionType="4"/>
              <ZoneGroupMember UUID="RINCON_BBB01400" ZoneName="Kitchen"
                Location="http://192.168.1.11:1400/xml/device_description.xml"
                EthLink="0" WifiEnabled="1" ConnectionType="5"/>
              <ZoneGroupMember UUID="RINCON_SUB01400" ZoneName="Living Room Sub"
                Location="http://192.168.1.12:1400/xml/device_description.xml" Invisible="1"
                EthLink="0" WifiEnabled="1" ConnectionType="5"/>
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

    [Fact]
    public void ParseTopologySnapshot_reads_eth_and_wifi_connection()
    {
        var snap = SonosDiscovery.ParseTopologySnapshot(ZoneGroupState);

        var living = snap.Members.Single(m => m.RoomName == "Living Room");
        Assert.True(living.EthLink);
        Assert.False(living.WifiEnabled);
        Assert.Equal(4, living.ConnectionType);
        Assert.Equal("ETH", living.ConnectionLabel);
        Assert.Contains("ETH", living.DisplayLabel, StringComparison.Ordinal);

        var kitchen = snap.Members.Single(m => m.RoomName == "Kitchen");
        Assert.False(kitchen.EthLink);
        Assert.True(kitchen.WifiEnabled);
        Assert.Equal(5, kitchen.ConnectionType);
        Assert.Equal("Wi‑Fi", kitchen.ConnectionLabel);

        var office = snap.Members.Single(m => m.RoomName == "Office");
        Assert.Null(office.EthLink);
        Assert.Null(office.ConnectionLabel);
    }
}

public class SonosDeviceInfoTests
{
    [Fact]
    public void ParseProductName_prefers_displayName()
    {
        const string xml = """
            <root><device>
              <displayName>Port</displayName>
              <modelName>Sonos Port</modelName>
            </device></root>
            """;
        Assert.Equal("Port", HotSonos.Core.SonosDeviceInfo.ParseProductName(xml));
    }

    [Fact]
    public void ParseProductName_strips_sonos_prefix_from_model()
    {
        const string xml = """
            <root><device>
              <modelName>Sonos Era 100</modelName>
            </device></root>
            """;
        Assert.Equal("Era 100", HotSonos.Core.SonosDeviceInfo.ParseProductName(xml));
    }
}

