using HotSonos.Core.Models;

namespace HotSonos.Core.Tests;

public class SonosControllerParseFavoritesTests
{
    [Fact]
    public void Parses_favorites_and_marks_shortcut_without_res()
    {
        const string didl = """
            <DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/"
                       xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"
                       xmlns:r="urn:schemas-rinconnetworks-com:metadata-1-0/"
                       xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">
              <item id="FV:2/1" parentID="FV:2" restricted="true">
                <dc:title>Morning Jazz</dc:title>
                <res protocolInfo="x-rincon-mp3radio:*:*:*">x-sonosapi-stream:s123?sid=254</res>
                <r:resMD>&lt;DIDL-Lite&gt;&lt;item id="0"&gt;&lt;/item&gt;&lt;/DIDL-Lite&gt;</r:resMD>
              </item>
              <item id="FV:2/2" parentID="FV:2" restricted="true">
                <dc:title>Sonos Radio Promo</dc:title>
              </item>
            </DIDL-Lite>
            """;

        var list = SonosController.ParseFavorites(didl, SonosFavoriteKind.Favorite);

        Assert.Equal(2, list.Count);
        Assert.Equal("Morning Jazz", list[0].Title);
        Assert.True(list[0].IsPlayable);
        Assert.Equal("Sonos Radio Promo", list[1].Title);
        Assert.False(list[1].IsPlayable);
        Assert.Contains("DIDL-Lite", list[0].Metadata);
    }

    [Fact]
    public void Parses_playlist_containers_as_playable_by_id()
    {
        const string didl = """
            <DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/"
                       xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"
                       xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">
              <container id="SQ:0" parentID="SQ:" restricted="true">
                <dc:title>Weekday Mix</dc:title>
                <res protocolInfo="file:*:audio/mpegurl:*">file:///jffs/settings/savedqueues.rsq#0</res>
              </container>
              <container id="SQ:1" parentID="SQ:" restricted="true">
                <dc:title>Empty Res Playlist</dc:title>
              </container>
            </DIDL-Lite>
            """;

        var list = SonosController.ParseFavorites(didl, SonosFavoriteKind.Playlist);

        Assert.Equal(2, list.Count);
        Assert.All(list, f => Assert.Equal(SonosFavoriteKind.Playlist, f.Kind));
        Assert.Equal("SQ:0", list[0].Id);
        Assert.True(list[0].IsPlayable);
        Assert.Equal("SQ:1", list[1].Id);
        Assert.True(list[1].IsPlayable); // id-based playback path
    }
}
