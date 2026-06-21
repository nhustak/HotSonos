using System.Net.Http;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace HotSonos.Core;

/// <summary>
/// Low-level SOAP transport for Sonos UPnP control. Builds the envelope, POSTs
/// it to http://{ip}:1400{controlPath}, and returns the parsed response body.
/// One instance can serve every speaker on the LAN.
/// </summary>
public sealed class SonosSoapClient
{
    private readonly HttpClient _http;

    public SonosSoapClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>
    /// Invokes <paramref name="action"/> on <paramref name="service"/> at the
    /// given speaker IP with the supplied (name, value) arguments in order.
    /// Returns the parsed SOAP response (the full envelope as an XDocument).
    /// </summary>
    public async Task<XDocument> InvokeAsync(
        string ip,
        SonosService service,
        string action,
        IEnumerable<KeyValuePair<string, string>> args,
        CancellationToken ct = default)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        body.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
        body.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        body.Append($"<u:{action} xmlns:u=\"{service.Type}\">");
        foreach (var arg in args)
        {
            body.Append($"<{arg.Key}>{SecurityElement.Escape(arg.Value)}</{arg.Key}>");
        }
        body.Append($"</u:{action}></s:Body></s:Envelope>");

        var url = $"http://{ip}:1400{service.ControlPath}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml"),
        };
        // SOAPACTION must be quoted and is case-sensitive.
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service.Type}#{action}\"");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // UPnP errors come back as 500 with a SOAP Fault; surface the body to help debugging.
            throw new SonosSoapException(
                $"SOAP {action} on {service.Type} at {ip} failed: HTTP {(int)response.StatusCode}. Body: {Truncate(text)}");
        }

        return XDocument.Parse(text);
    }

    /// <summary>
    /// Reads the text value of the first descendant output element whose local
    /// name matches <paramref name="localName"/> (namespace-agnostic).
    /// </summary>
    public static string? ReadValue(XDocument response, string localName) =>
        response.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == localName)?
            .Value;

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}

/// <summary>Thrown when a Sonos SOAP call returns a non-success status.</summary>
public sealed class SonosSoapException(string message) : Exception(message);
