using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HotSonos.Core;

/// <summary>Spotify account link started via Sonos SMAPI <c>getAppLink</c>. Open <see cref="RegUrl"/> in a browser.</summary>
public sealed record SpotifyLinkChallenge(
    string RegUrl,
    string LinkCode,
    string? AppUrl,
    string HouseholdId,
    string DeviceId,
    string SecureUri,
    int ServiceType);

/// <summary>SMAPI session used to list Spotify playlists. The speaker still plays from its own linked account.</summary>
public sealed class SpotifySession
{
    public int ServiceType { get; set; } = SpotifyItem.DefaultServiceType;
    public string SecureUri { get; set; } = "";
    public string Token { get; set; } = "";
    public string Key { get; set; } = "";
    public string HouseholdId { get; set; } = "";
    public string DeviceId { get; set; } = "";

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Token)
        && !string.IsNullOrWhiteSpace(SecureUri)
        && !string.IsNullOrWhiteSpace(HouseholdId)
        && !string.IsNullOrWhiteSpace(DeviceId);
}

/// <summary>One row from a SMAPI <c>getMetadata</c> result.</summary>
public sealed record SpotifyMetadataItem(string Id, string ItemType, string Title, bool CanPlay);

/// <summary>Parses Sonos SMAPI and speaker account-discovery XML. No network.</summary>
public static class SpotifySmapiXml
{
    public static (string? Mac, string? HouseholdId) ReadSpeakerIdentity(string zpXml)
    {
        var doc = Parse(zpXml);
        return (Child(doc, "MACAddress"), Child(doc, "HouseholdControlID"));
    }

    public static (string SecureUri, int ServiceType)? FindSpotifyService(string descriptorXmlOrSoap)
    {
        var text = descriptorXmlOrSoap;
        if (text.Contains("AvailableServiceDescriptorList", StringComparison.Ordinal))
        {
            var soap = Parse(text);
            text = Child(soap, "AvailableServiceDescriptorList") ?? "";
            text = System.Net.WebUtility.HtmlDecode(text);
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var doc = Parse(text);
        foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "Service"))
        {
            var name = (string?)service.Attribute("Name");
            if (!string.Equals(name, "Spotify", StringComparison.OrdinalIgnoreCase))
                continue;

            var idText = (string?)service.Attribute("Id");
            var secure = (string?)service.Attribute("SecureUri");
            if (string.IsNullOrWhiteSpace(secure))
                secure = (string?)service.Attribute("Uri");
            if (!int.TryParse(idText, out var id) || string.IsNullOrWhiteSpace(secure))
                return null;

            return (secure, id * 256 + 7);
        }

        return null;
    }

    public static SpotifyLinkChallenge? ReadLinkChallenge(
        string responseXml, string householdId, string deviceId, string secureUri, int serviceType)
    {
        var doc = Parse(responseXml);
        var regUrl = Child(doc, "regUrl");
        var linkCode = Child(doc, "linkCode");
        if (string.IsNullOrWhiteSpace(regUrl) || string.IsNullOrWhiteSpace(linkCode))
            return null;

        return new SpotifyLinkChallenge(
            regUrl,
            linkCode,
            Child(doc, "appUrl"),
            householdId,
            deviceId,
            secureUri,
            serviceType);
    }

    public static SpotifySession? ReadSession(string responseXml, SpotifyLinkChallenge challenge)
    {
        var doc = Parse(responseXml);
        var token = Child(doc, "authToken") ?? Child(doc, "token");
        var key = Child(doc, "privateKey") ?? Child(doc, "key") ?? "";
        if (string.IsNullOrWhiteSpace(token))
            return null;

        return new SpotifySession
        {
            ServiceType = challenge.ServiceType,
            SecureUri = challenge.SecureUri,
            Token = token,
            Key = key,
            HouseholdId = challenge.HouseholdId,
            DeviceId = challenge.DeviceId,
        };
    }

    public static IReadOnlyList<SpotifyMetadataItem> ReadMetadata(string responseXml)
    {
        var doc = Parse(responseXml);
        var items = new List<SpotifyMetadataItem>();
        foreach (var node in doc.Descendants().Where(e =>
                     e.Name.LocalName is "mediaCollection" or "mediaMetadata"))
        {
            var id = Child(node, "id") ?? "";
            var itemType = Child(node, "itemType") ?? "";
            var title = Child(node, "title") ?? "";
            var canPlay = string.Equals(Child(node, "canPlay"), "true", StringComparison.OrdinalIgnoreCase);
            if (id.Length == 0 && title.Length == 0)
                continue;
            items.Add(new SpotifyMetadataItem(id, itemType, title, canPlay));
        }

        return items;
    }

    public static string? ReadFault(string responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml))
            return null;
        try
        {
            var doc = Parse(responseXml);
            var text = Child(doc, "faultstring") ?? Child(doc, "errorstring") ?? Child(doc, "faultcode");
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public static bool IsPendingLinkFault(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("NOT_LINKED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not linked", StringComparison.OrdinalIgnoreCase)
            || message.Contains("pending", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Child(XContainer parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    /// <summary>
    /// Parses Sonos XML. The music-service catalog is HTML-decoded before the
    /// inner parse, which turns a name like "CBC Radio &amp; Music" into a bare
    /// ampersand. Repair those and parse again.
    /// </summary>
    private static XDocument Parse(string xml)
    {
        try
        {
            return XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException) when (xml.Contains('&'))
        {
            return XDocument.Parse(BareAmpersand.Replace(xml, "&amp;"));
        }
    }

    private static readonly Regex BareAmpersand = new(
        @"&(?!(?:[A-Za-z][A-Za-z0-9]*|#[0-9]+|#x[0-9A-Fa-f]+);)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

/// <summary>
/// Browses Spotify through the Sonos Music API endpoint the speaker advertises.
/// Listing needs a one-time app link. Playing a known Spotify URI does not.
/// </summary>
public sealed class SpotifySmapiClient
{
    private readonly HttpClient _http;

    public SpotifySmapiClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<SpotifyLinkChallenge> BeginLinkAsync(string speakerIp, CancellationToken ct = default)
    {
        var (householdId, deviceId, secureUri, serviceType) = await ReadServiceAsync(speakerIp, ct).ConfigureAwait(false);
        var body = $"""
            <getAppLink xmlns="http://www.sonos.com/Services/1.1">
              <householdId>{Escape(householdId)}</householdId>
              <hardware>WINDOWS</hardware>
              <osVersion>10</osVersion>
              <sonosAppName>HotSonos</sonosAppName>
            </getAppLink>
            """;
        var xml = await PostAsync(secureUri, "getAppLink", DeviceHeader(deviceId), body, ct).ConfigureAwait(false);
        var challenge = SpotifySmapiXml.ReadLinkChallenge(xml, householdId, deviceId, secureUri, serviceType);
        if (challenge is null)
            throw new InvalidOperationException("Spotify did not return a link URL. The service may already be linked a different way.");
        return challenge;
    }

    public async Task<SpotifySession?> TryCompleteLinkAsync(SpotifyLinkChallenge challenge, CancellationToken ct = default)
    {
        var body = $"""
            <getDeviceAuthToken xmlns="http://www.sonos.com/Services/1.1">
              <householdId>{Escape(challenge.HouseholdId)}</householdId>
              <linkCode>{Escape(challenge.LinkCode)}</linkCode>
              <linkDeviceId>{Escape(challenge.DeviceId)}</linkDeviceId>
            </getDeviceAuthToken>
            """;
        string xml;
        try
        {
            xml = await PostAsync(challenge.SecureUri, "getDeviceAuthToken", DeviceHeader(challenge.DeviceId), body, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (SpotifySmapiXml.IsPendingLinkFault(ex.Message))
        {
            return null;
        }

        var session = SpotifySmapiXml.ReadSession(xml, challenge);
        if (session is null || !session.IsComplete)
            return null;
        return session;
    }

    public async Task<IReadOnlyList<SpotifyItem>> ListPlaylistsAsync(SpotifySession session, CancellationToken ct = default)
    {
        if (!session.IsComplete)
            throw new InvalidOperationException("Spotify is not linked.");

        var root = await GetMetadataAsync(session, "root", 0, 100, ct).ConfigureAwait(false);
        var direct = PlaylistsOf(root);
        var folder = root.FirstOrDefault(i =>
            (i.ItemType.Contains("collection", StringComparison.OrdinalIgnoreCase)
                || i.ItemType.Contains("container", StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.ItemType, "playlistList", StringComparison.OrdinalIgnoreCase))
            && (i.Title.Contains("playlist", StringComparison.OrdinalIgnoreCase)
                || i.Id.Contains("playlist", StringComparison.OrdinalIgnoreCase)));

        if (folder is null)
            return direct;

        var collected = new List<SpotifyItem>();
        var index = 0;
        const int pageSize = 100;
        while (collected.Count < 300)
        {
            var page = await GetMetadataAsync(session, folder.Id, index, pageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
                break;
            collected.AddRange(PlaylistsOf(page));
            index += page.Count;
            if (page.Count < pageSize)
                break;
        }

        return collected.Count > 0 ? collected : direct;
    }

    private async Task<(string HouseholdId, string DeviceId, string SecureUri, int ServiceType)> ReadServiceAsync(
        string speakerIp, CancellationToken ct)
    {
        var zp = await GetTextAsync($"http://{speakerIp}:1400/status/zp", ct).ConfigureAwait(false);
        var (mac, household) = SpotifySmapiXml.ReadSpeakerIdentity(zp);
        if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(household))
            throw new InvalidOperationException("This speaker did not report a household id, so Spotify cannot be linked.");

        var soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body>
            <u:ListAvailableServices xmlns:u="urn:schemas-upnp-org:service:MusicServices:1"></u:ListAvailableServices>
            </s:Body></s:Envelope>
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{speakerIp}:1400/MusicServices/Control")
        {
            Content = new StringContent(soap, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", "\"urn:schemas-upnp-org:service:MusicServices:1#ListAvailableServices\"");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Could not read the Sonos music-service list.");

        var spotify = SpotifySmapiXml.FindSpotifyService(text);
        if (spotify is null)
            throw new InvalidOperationException("Spotify is not in this Sonos system's music services.");
        return (household, mac, spotify.Value.SecureUri, spotify.Value.ServiceType);
    }

    private async Task<IReadOnlyList<SpotifyMetadataItem>> GetMetadataAsync(
        SpotifySession session, string id, int index, int count, CancellationToken ct)
    {
        var body = $"""
            <getMetadata xmlns="http://www.sonos.com/Services/1.1">
              <id>{Escape(id)}</id>
              <index>{index}</index>
              <count>{count}</count>
            </getMetadata>
            """;
        var xml = await PostAsync(session.SecureUri, "getMetadata", LoginHeader(session), body, ct).ConfigureAwait(false);
        return SpotifySmapiXml.ReadMetadata(xml);
    }

    private async Task<string> PostAsync(
        string url, string action, string headerInner, string bodyInner, CancellationToken ct)
    {
        var envelope = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Header>{headerInner}</soap:Header>
              <soap:Body>{bodyInner}</soap:Body>
            </soap:Envelope>
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"http://www.sonos.com/Services/1.1#{action}\"");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var fault = SpotifySmapiXml.ReadFault(text);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(fault)
                ? $"Spotify {action} failed ({(int)response.StatusCode})."
                : fault);
        }

        return text;
    }

    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string DeviceHeader(string deviceId) =>
        "<credentials xmlns=\"http://www.sonos.com/Services/1.1\">" +
        $"<deviceId>{Escape(deviceId)}</deviceId><deviceProvider>Sonos</deviceProvider></credentials>";

    private static string LoginHeader(SpotifySession session) =>
        "<credentials xmlns=\"http://www.sonos.com/Services/1.1\">" +
        $"<deviceId>{Escape(session.DeviceId)}</deviceId><deviceProvider>Sonos</deviceProvider>" +
        "<loginToken>" +
        $"<token>{Escape(session.Token)}</token>" +
        $"<key>{Escape(session.Key)}</key>" +
        $"<householdId>{Escape(session.HouseholdId)}</householdId>" +
        "</loginToken></credentials>";

    private static IReadOnlyList<SpotifyItem> PlaylistsOf(IReadOnlyList<SpotifyMetadataItem> items)
    {
        var list = new List<SpotifyItem>();
        foreach (var item in items)
        {
            if (!item.ItemType.Contains("playlist", StringComparison.OrdinalIgnoreCase) && !item.CanPlay)
                continue;
            if (!SpotifyItem.TryParse(item.Id, out var parsed)
                && !SpotifyItem.TryParse($"spotify:playlist:{item.Id}", out parsed))
                continue;
            if (parsed is null || !string.Equals(parsed.Kind, "playlist", StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(parsed with { Title = string.IsNullOrWhiteSpace(item.Title) ? parsed.Canonical : item.Title });
        }

        return list;
    }

    private static string Escape(string value) => SecurityEscape(value);

    private static string SecurityEscape(string value) =>
        System.Security.SecurityElement.Escape(value) ?? "";
}
