using System.Globalization;

namespace HotSonos.Remote.Protocol;

/// <summary>
/// Pairing link encoded in the QR code on the PC Phone window.
/// <c>hotsonos://pair?host=192.168.1.10&amp;port=42342&amp;pin=042815</c>
/// The phone scans it, opens it, or pastes it, then saves and tests the connection.
/// </summary>
public static class PhonePairLink
{
    public const string Scheme = "hotsonos";
    public const string PairHost = "pair";

    /// <summary>
    /// MAUI preference keys for the saved PC. These strings are the on-device
    /// record. Renaming one makes an update look unpaired. The values survive
    /// an update of application id <c>com.tangenttech.hotsonos</c> and are gone
    /// if that id changes, because Android and iOS treat that as a different app.
    /// </summary>
    public const string HostPreferenceKey = "pc.host";
    public const string PortPreferenceKey = "pc.port";
    public const string PinPreferenceKey = "pc.pin";

    public static bool TryFormat(string? host, int port, string? pin, out string link, out string? error)
    {
        link = "";
        if (!RemoteNet.TryParseAddress(host, port.ToString(CultureInfo.InvariantCulture), out var parsedHost, out var parsedPort, out error))
            return false;

        var parsedPin = (pin ?? "").Trim();
        if (!RemoteNet.IsSixDigitPin(parsedPin))
        {
            error = "PIN must be 6 digits.";
            return false;
        }

        link = Scheme + "://" + PairHost
            + "?host=" + Uri.EscapeDataString(parsedHost)
            + "&port=" + parsedPort.ToString(CultureInfo.InvariantCulture)
            + "&pin=" + Uri.EscapeDataString(parsedPin);
        error = null;
        return true;
    }

    public static bool TryParse(string? text, out string host, out int port, out string pin, out string? error)
    {
        host = "";
        port = RemoteApi.DefaultPort;
        pin = "";
        var raw = (text ?? "").Trim().Trim('"', '\'');
        if (raw.Length == 0)
        {
            error = "Paste the pairing link from the PC, or scan the code.";
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            || !IsPair(uri))
        {
            error = "That is not a HotSonos pairing link.";
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("host", out var hostValue) || string.IsNullOrWhiteSpace(hostValue))
        {
            error = "The pairing link has no PC address.";
            return false;
        }

        query.TryGetValue("port", out var portValue);
        query.TryGetValue("pin", out var pinValue);
        if (!RemoteNet.TryParseAddress(hostValue, portValue, out host, out port, out error))
            return false;

        pin = (pinValue ?? "").Trim();
        if (!RemoteNet.IsSixDigitPin(pin))
        {
            error = "The pairing link PIN must be 6 digits.";
            return false;
        }

        error = null;
        return true;
    }

    static bool IsPair(Uri uri)
    {
        if (uri.Host.Equals(PairHost, StringComparison.OrdinalIgnoreCase))
            return true;

        var path = uri.AbsolutePath.Trim('/');
        return string.IsNullOrEmpty(uri.Host)
            && path.Equals(PairHost, StringComparison.OrdinalIgnoreCase);
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = query.TrimStart('?');
        if (text.Length == 0)
            return map;

        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]).Trim();
            var value = Uri.UnescapeDataString(eq < 0 ? "" : part[(eq + 1)..]).Trim();
            if (key.Length > 0)
                map[key] = value;
        }

        return map;
    }
}
