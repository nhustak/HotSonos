using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HotSonos.Core;

/// <summary>
/// Lightweight product info from <c>/xml/device_description.xml</c>
/// (displayName / modelName) for topology chips.
/// </summary>
public static partial class SonosDeviceInfo
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>
    /// Short product label for UI, e.g. Port, Era 100, One.
    /// Prefers <c>displayName</c>, else strips "Sonos " from <c>modelName</c>.
    /// </summary>
    public static async Task<string?> GetProductNameAsync(string ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(900));
            var xml = await Http.GetStringAsync($"http://{ip}:1400/xml/device_description.xml", cts.Token)
                .ConfigureAwait(false);
            return ParseProductName(xml);
        }
        catch
        {
            return null;
        }
    }

    public static string? ParseProductName(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        // Prefer local-name match (namespace-agnostic).
        try
        {
            var doc = XDocument.Parse(xml);
            var display = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "displayName")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(display))
                return display;
            var model = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "modelName")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(model))
            {
                if (model.StartsWith("Sonos ", StringComparison.OrdinalIgnoreCase))
                    return model["Sonos ".Length..].Trim();
                return model;
            }
        }
        catch
        {
            // fall through to regex
        }

        var mDisp = DisplayNameRegex().Match(xml);
        if (mDisp.Success)
            return mDisp.Groups[1].Value.Trim();
        var mModel = ModelNameRegex().Match(xml);
        if (mModel.Success)
        {
            var model = mModel.Groups[1].Value.Trim();
            if (model.StartsWith("Sonos ", StringComparison.OrdinalIgnoreCase))
                return model["Sonos ".Length..].Trim();
            return model;
        }

        return null;
    }

    [GeneratedRegex(@"<displayName>([^<]+)</displayName>", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayNameRegex();

    [GeneratedRegex(@"<modelName>([^<]+)</modelName>", RegexOptions.IgnoreCase)]
    private static partial Regex ModelNameRegex();
}
