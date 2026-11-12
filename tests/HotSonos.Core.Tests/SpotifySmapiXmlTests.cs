using HotSonos.Core;

namespace HotSonos.Core.Tests;

public class SpotifySmapiXmlTests
{
    [Fact]
    public void Finds_spotify_service_type_from_descriptor_list()
    {
        const string soap = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <AvailableServiceDescriptorList>&lt;Services&gt;&lt;Service Id="12" Name="Spotify" SecureUri="https://spotify-v5.ws.sonos.com/smapi" /&gt;&lt;/Services&gt;</AvailableServiceDescriptorList>
              </s:Body>
            </s:Envelope>
            """;

        var found = SpotifySmapiXml.FindSpotifyService(soap);

        Assert.NotNull(found);
        Assert.Equal("https://spotify-v5.ws.sonos.com/smapi", found.Value.SecureUri);
        Assert.Equal(3079, found.Value.ServiceType);
    }

    [Fact]
    public void Reads_link_challenge_and_session_without_keeping_extra_secrets_in_the_parse()
    {
        const string link = """
            <getAppLinkResponse xmlns="http://www.sonos.com/Services/1.1">
              <getAppLinkResult>
                <authorizeAccount>
                  <appUrl>spotify://link</appUrl>
                  <deviceLink>
                    <regUrl>https://example.test/link</regUrl>
                    <linkCode>Ab12</linkCode>
                  </deviceLink>
                </authorizeAccount>
              </getAppLinkResult>
            </getAppLinkResponse>
            """;

        var challenge = SpotifySmapiXml.ReadLinkChallenge(link, "hh", "AA-BB", "https://spotify.example/smapi", 3079);
        Assert.NotNull(challenge);
        Assert.Equal("https://example.test/link", challenge!.RegUrl);
        Assert.Equal("Ab12", challenge.LinkCode);

        const string auth = """
            <getDeviceAuthTokenResponse xmlns="http://www.sonos.com/Services/1.1">
              <getDeviceAuthTokenResult>
                <authToken>token-value</authToken>
                <privateKey>key-value</privateKey>
              </getDeviceAuthTokenResult>
            </getDeviceAuthTokenResponse>
            """;

        var session = SpotifySmapiXml.ReadSession(auth, challenge);
        Assert.NotNull(session);
        Assert.True(session!.IsComplete);
        Assert.Equal(3079, session.ServiceType);
        Assert.Equal("token-value", session.Token);
    }

    [Fact]
    public void Reads_playlist_rows_and_pending_faults()
    {
        const string meta = """
            <getMetadataResponse xmlns="http://www.sonos.com/Services/1.1">
              <getMetadataResult>
                <mediaCollection>
                  <id>playlists</id>
                  <itemType>collection</itemType>
                  <title>Playlists</title>
                  <canPlay>false</canPlay>
                </mediaCollection>
                <mediaCollection>
                  <id>spotify:playlist:37i9dQZF1DXcBWIGoYBM5M</id>
                  <itemType>playlist</itemType>
                  <title>Discover Weekly</title>
                  <canPlay>true</canPlay>
                </mediaCollection>
              </getMetadataResult>
            </getMetadataResponse>
            """;

        var items = SpotifySmapiXml.ReadMetadata(meta);
        Assert.Equal(2, items.Count);
        Assert.Equal("Playlists", items[0].Title);
        Assert.Equal("playlist", items[1].ItemType);

        Assert.True(SpotifySmapiXml.IsPendingLinkFault("Client.NOT_LINKED"));
        Assert.Equal(
            "Not linked",
            SpotifySmapiXml.ReadFault("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body><soap:Fault><faultstring>Not linked</faultstring></soap:Fault></soap:Body>
                </soap:Envelope>
                """));
    }

    [Fact]
    public void Finds_spotify_when_another_service_name_contains_an_ampersand()
    {
        // SOAP stores the catalog escaped. One decode leaves "&amp;" and
        // HtmlDecode then leaves a bare "&" in "CBC Radio & Music".
        const string soap = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <AvailableServiceDescriptorList>&lt;Services&gt;&lt;Service Id="256" Name="CBC Radio &amp;amp; Music" Uri="http://example.test/" /&gt;&lt;Service Id="12" Name="Spotify" SecureUri="https://spotify-v5.ws.sonos.com/smapi" /&gt;&lt;/Services&gt;</AvailableServiceDescriptorList>
              </s:Body>
            </s:Envelope>
            """;

        var found = SpotifySmapiXml.FindSpotifyService(soap);

        Assert.NotNull(found);
        Assert.Equal(3079, found.Value.ServiceType);
    }

    [Fact]
    public void Reads_link_url_that_contains_a_query_ampersand()
    {
        const string link = """
            <getAppLinkResponse xmlns="http://www.sonos.com/Services/1.1">
              <getAppLinkResult>
                <authorizeAccount>
                  <deviceLink>
                    <regUrl>https://example.test/link?a=1&b=2</regUrl>
                    <linkCode>Ab12</linkCode>
                  </deviceLink>
                </authorizeAccount>
              </getAppLinkResult>
            </getAppLinkResponse>
            """;

        var challenge = SpotifySmapiXml.ReadLinkChallenge(link, "hh", "device", "https://spotify.example/smapi", 3079);

        Assert.NotNull(challenge);
        Assert.Equal("https://example.test/link?a=1&b=2", challenge!.RegUrl);
    }
}
