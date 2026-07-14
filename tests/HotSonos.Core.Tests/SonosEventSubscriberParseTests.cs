using HotSonos.Core.Models;

namespace HotSonos.Core.Tests;

public class SonosEventSubscriberParseTests
{
    [Fact]
    public void ParseNotify_extracts_title_artist_art_and_state()
    {
        // LastChange is HTML-escaped inside the NOTIFY body, as Sonos sends it.
        const string request = """
            NOTIFY /notify HTTP/1.1
            CONTENT-TYPE: text/xml

            <e:propertyset xmlns:e="urn:schemas-upnp-org:event-1-0">
              <e:property>
                <LastChange>
                  &lt;Event xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/AVT/&quot;&gt;
                    &lt;InstanceID val=&quot;0&quot;&gt;
                      &lt;TransportState val=&quot;PLAYING&quot;/&gt;
                      &lt;CurrentTrackMetaData val=&quot;&amp;lt;DIDL-Lite xmlns:dc=&amp;quot;http://purl.org/dc/elements/1.1/&amp;quot; xmlns:upnp=&amp;quot;urn:schemas-upnp-org:metadata-1-0/upnp/&amp;quot; xmlns=&amp;quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&amp;quot;&amp;gt;&amp;lt;item&amp;gt;&amp;lt;dc:title&amp;gt;Blue in Green&amp;lt;/dc:title&amp;gt;&amp;lt;dc:creator&amp;gt;Miles Davis&amp;lt;/dc:creator&amp;gt;&amp;lt;upnp:album&amp;gt;Kind of Blue&amp;lt;/upnp:album&amp;gt;&amp;lt;upnp:albumArtURI&amp;gt;/getaa?u=track&amp;lt;/upnp:albumArtURI&amp;gt;&amp;lt;/item&amp;gt;&amp;lt;/DIDL-Lite&amp;gt;&quot;/&gt;
                    &lt;/InstanceID&gt;
                  &lt;/Event&gt;
                </LastChange>
              </e:property>
            </e:propertyset>
            """;

        var np = SonosEventSubscriber.ParseNotify(request, "192.168.1.10");

        Assert.NotNull(np);
        Assert.Equal(SonosTransportState.Playing, np!.State);
        Assert.Equal("Blue in Green", np.Title);
        Assert.Equal("Miles Davis", np.Artist);
        Assert.Equal("Kind of Blue", np.Album);
        Assert.Equal("http://192.168.1.10:1400/getaa?u=track", np.AlbumArtUri);
    }

    [Fact]
    public void ParseNotify_without_metadata_returns_state_only()
    {
        const string request = """
            <LastChange>
              &lt;Event&gt;&lt;InstanceID val=&quot;0&quot;&gt;&lt;TransportState val=&quot;PAUSED_PLAYBACK&quot;/&gt;&lt;/InstanceID&gt;&lt;/Event&gt;
            </LastChange>
            """;

        var np = SonosEventSubscriber.ParseNotify(request, null);

        Assert.NotNull(np);
        Assert.Equal(SonosTransportState.PausedPlayback, np!.State);
        Assert.True(np.IsEmpty);
    }

    [Fact]
    public void ExtractTopology_decodes_inner_zone_group_state()
    {
        const string request = """
            <e:property>
              <ZoneGroupState>
                &lt;ZoneGroupState&gt;&lt;ZoneGroups/&gt;&lt;/ZoneGroupState&gt;
              </ZoneGroupState>
            </e:property>
            """;

        var xml = SonosEventSubscriber.ExtractTopology(request);

        Assert.NotNull(xml);
        Assert.Contains("ZoneGroups", xml, StringComparison.Ordinal);
    }
}
