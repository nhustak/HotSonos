using System.Net;
using System.Net.Sockets;

namespace HotSonos.Remote.Protocol;

/// <summary>
/// LAN announcement for the phone. The PC binds UDP on the same address as the HTTP listener.
/// The reply is the address and port only. The PIN is never announced.
/// </summary>
public static class RemoteDiscovery
{
    public const int Port = 42343;
    public const string Probe = "HS1?";

    public static bool IsProbe(string? text) =>
        string.Equals((text ?? "").Trim(), Probe, StringComparison.Ordinal);

    public static string BuildReply(string ip, int httpPort) =>
        "HS1 " + ip.Trim() + " " + httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParseReply(string? text, out string host, out int port)
    {
        host = "";
        port = 0;
        var parts = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !parts[0].Equals("HS1", StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(parts[1]) || parts[1].Contains('/') || parts[1].Any(char.IsWhiteSpace))
            return false;
        if (!int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
            || port is < 1024 or > 65535)
            return false;
        host = parts[1];
        return true;
    }

    /// <summary>/24 directed broadcast. The phone listener already treats the house as a /24.</summary>
    public static IPAddress? DirectedBroadcast(string? ipv4)
    {
        if (!IPAddress.TryParse(ipv4?.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        if (IPAddress.IsLoopback(ip))
            return null;
        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 169 && bytes[1] == 254)
            return null;
        bytes[3] = 255;
        return new IPAddress(bytes);
    }

    /// <summary>Limited broadcast plus one directed broadcast per local /24.</summary>
    public static IReadOnlyList<IPAddress> ProbeTargets(IEnumerable<string>? localIpv4)
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        if (localIpv4 is null)
            return list;
        foreach (var raw in localIpv4)
        {
            var directed = DirectedBroadcast(raw);
            if (directed is null)
                continue;
            if (list.Any(existing => existing.Equals(directed)))
                continue;
            list.Add(directed);
        }

        return list;
    }
}
