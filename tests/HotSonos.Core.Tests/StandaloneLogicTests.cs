using HotSonos.Core.Models;

namespace HotSonos.Core.Tests;

public class StandaloneLogicTests
{
    [Fact]
    public void Criteria_StripsQuotesAndSearchesTitleArtistAlbum()
    {
        var criteria = SonosLibraryQuery.Criteria("kind of \"blue\"");
        Assert.Equal(
            "dc:title contains \"kind of blue\" or dc:creator contains \"kind of blue\" or upnp:album contains \"kind of blue\"",
            criteria);
    }

    [Fact]
    public void SampleStarts_StayInsideTheLibrary()
    {
        var starts = SonosLibraryQuery.SampleStarts(1000, 25, 6, new Random(1));
        Assert.Equal(6, starts.Count);
        Assert.All(starts, start => Assert.InRange(start, 0, 975));
        Assert.Equal(starts.OrderBy(s => s), starts);
    }

    [Fact]
    public void SampleStarts_ReturnsNothingForAnEmptyLibrary()
    {
        Assert.Empty(SonosLibraryQuery.SampleStarts(0, 25, 6, new Random(1)));
    }

    [Fact]
    public void FormatRelTime_UsesSonosHoursMinutesSeconds()
    {
        Assert.Equal("0:01:30", SonosLibraryQuery.FormatRelTime(TimeSpan.FromSeconds(90)));
        Assert.Equal("1:00:05", SonosLibraryQuery.FormatRelTime(TimeSpan.FromSeconds(3605)));
        Assert.Equal("0:00:00", SonosLibraryQuery.FormatRelTime(TimeSpan.FromSeconds(-4)));
    }

    [Fact]
    public void FollowersToRejoin_KeepsTheOldGroupAndSkipsTheNewLeader()
    {
        var kitchen = Zone("Kitchen", "K", "K");
        var office = Zone("Office", "O", "K");
        var den = Zone("Den", "D", "D");
        var followers = SonosGrouping.FollowersToRejoin([kitchen, office, den], office);
        Assert.Equal(["Kitchen"], followers.Select(z => z.RoomName));
    }

    [Fact]
    public void OthersToJoin_SkipsRoomsAlreadyUnderThatLeader()
    {
        var kitchen = Zone("Kitchen", "K", "K");
        var office = Zone("Office", "O", "K");
        var den = Zone("Den", "D", "D");
        var others = SonosGrouping.OthersToJoin([kitchen, office, den], kitchen);
        Assert.Equal(["Den"], others.Select(z => z.RoomName));
    }

    [Fact]
    public void RinconUri_AddressesTheCoordinator()
    {
        Assert.Equal("x-rincon:RINCON_1", SonosGrouping.RinconUri("RINCON_1"));
    }

    [Fact]
    public void ParseLibraryTracks_ReadsTitleArtistAlbumAndUri()
    {
        const string didl =
            """
            <DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/" xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">
              <item id="A:TRACKS/1">
                <dc:title>So What</dc:title>
                <dc:creator>Miles Davis</dc:creator>
                <upnp:album>Kind of Blue</upnp:album>
                <res>x-file-cifs://nas/Music/so-what.flac</res>
              </item>
            </DIDL-Lite>
            """;
        var track = Assert.Single(SonosController.ParseLibraryTracks(didl));
        Assert.Equal("So What", track.Title);
        Assert.Equal("Miles Davis", track.Artist);
        Assert.Equal("Kind of Blue", track.Album);
        Assert.Equal("x-file-cifs://nas/Music/so-what.flac", track.Uri);
    }

    static SonosZone Zone(string room, string uuid, string coordinator) => new()
    {
        RoomName = room,
        Uuid = uuid,
        IpAddress = "10.0.0." + uuid[0],
        CoordinatorUuid = coordinator,
        CoordinatorIpAddress = "10.0.0." + coordinator[0],
        GroupId = "G" + coordinator,
    };
}
