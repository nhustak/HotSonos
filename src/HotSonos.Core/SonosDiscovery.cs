using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>
/// Finds Sonos players on the LAN via SSDP, then resolves the full zone/group
/// topology (room names + group coordinators) from any one responding player.
/// </summary>
public sealed class SonosDiscovery
{
    private const string SsdpMulticastAddress = "239.255.255.250";
    private const int SsdpPort = 1900;

    // Sonos players answer this device search target.
    private const string SearchTarget = "urn:schemas-upnp-org:device:ZonePlayer:1";

    private readonly SonosSoapClient _soap;

    public SonosDiscovery(SonosSoapClient? soap = null) => _soap = soap ?? new SonosSoapClient();

    /// <summary>
    /// Discovers all zones on the network: broadcasts an SSDP M-SEARCH, takes
    /// the first responding player, and asks it for the whole topology.
    /// Returns an empty list if no player answers within <paramref name="timeout"/>.
    /// </summary>
    public async Task<IReadOnlyList<SonosZone>> DiscoverZonesAsync(
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = timeout ?? TimeSpan.FromSeconds(3);
        var ips = await SearchAsync(deadline, ct).ConfigureAwait(false);
        foreach (var ip in ips)
        {
            try
            {
                var zones = await GetZonesFromAsync(ip, ct).ConfigureAwait(false);
                if (zones.Count > 0)
                    return zones;
            }
            catch
            {
                // Try the next responder rather than failing the whole discovery.
            }
        }
        return [];
    }

    /// <summary>
    /// Resolves the full topology starting from a known player IP (skips SSDP).
    /// Useful when SSDP is blocked or when a speaker IP is already configured.
    /// </summary>
    public async Task<IReadOnlyList<SonosZone>> GetZonesFromAsync(string ip, CancellationToken ct = default)
    {
        var response = await _soap.InvokeAsync(
            ip, SonosService.ZoneGroupTopology, "GetZoneGroupState",
            [], ct).ConfigureAwait(false);

        var stateXml = SonosSoapClient.ReadValue(response, "ZoneGroupState");
        if (string.IsNullOrWhiteSpace(stateXml))
            return [];

        return ParseZoneGroupState(stateXml);
    }

    /// <summary>Parses the (already-unescaped) ZoneGroupState XML into zones.</summary>
    public static IReadOnlyList<SonosZone> ParseZoneGroupState(string stateXml)
    {
        var doc = XDocument.Parse(stateXml);
        var zones = new List<SonosZone>();

        foreach (var group in doc.Descendants().Where(e => e.Name.LocalName == "ZoneGroup"))
        {
            var coordinatorUuid = (string?)group.Attribute("Coordinator") ?? "";
            var groupId = (string?)group.Attribute("ID") ?? "";

            var members = group.Elements()
                .Where(e => e.Name.LocalName == "ZoneGroupMember")
                .ToList();

            // The coordinator member tells us where commands should be sent.
            var coordinator = members.FirstOrDefault(m =>
                string.Equals((string?)m.Attribute("UUID"), coordinatorUuid, StringComparison.OrdinalIgnoreCase));
            var coordinatorIp = HostFromLocation((string?)coordinator?.Attribute("Location"));

            foreach (var member in members)
            {
                // Skip invisible members (surround speakers, subs, bridges).
                if ((string?)member.Attribute("Invisible") == "1")
                    continue;

                var uuid = (string?)member.Attribute("UUID");
                var zoneName = (string?)member.Attribute("ZoneName");
                var ip = HostFromLocation((string?)member.Attribute("Location"));
                if (uuid is null || zoneName is null || ip is null || coordinatorIp is null)
                    continue;

                zones.Add(new SonosZone
                {
                    RoomName = zoneName,
                    Uuid = uuid,
                    IpAddress = ip,
                    CoordinatorUuid = coordinatorUuid,
                    CoordinatorIpAddress = coordinatorIp,
                    GroupId = groupId,
                });
            }
        }

        return zones;
    }

    private static string? HostFromLocation(string? location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>
    /// Sends SSDP M-SEARCH from every usable local IPv4 interface and collects
    /// the IPs of responders. Probing per-interface is essential on multi-homed
    /// machines (VPN/WSL/Hyper-V adapters) where a single default-route probe
    /// goes out the wrong interface and never reaches the LAN.
    /// </summary>
    private static async Task<IReadOnlyList<string>> SearchAsync(TimeSpan timeout, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localAddresses = GetLocalIPv4Addresses();
        if (localAddresses.Count == 0)
            return [];

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var tasks = localAddresses.Select(addr => ProbeInterfaceAsync(addr, found, cts.Token));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return found.ToList();
    }

    /// <summary>Sends probes from one local interface and records responders into <paramref name="found"/>.</summary>
    private static async Task ProbeInterfaceAsync(IPAddress localAddress, HashSet<string> found, CancellationToken ct)
    {
        var message =
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {SsdpMulticastAddress}:{SsdpPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 1\r\n" +
            $"ST: {SearchTarget}\r\n\r\n";
        var payload = Encoding.ASCII.GetBytes(message);
        var multicast = new IPEndPoint(IPAddress.Parse(SsdpMulticastAddress), SsdpPort);

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(localAddress, 0));
            // Pin multicast egress to this interface.
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                localAddress.GetAddressBytes());

            // Fire three probes spaced over the window to tolerate UDP loss.
            var sender = Task.Run(async () =>
            {
                try
                {
                    for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
                    {
                        await udp.SendAsync(payload, payload.Length, multicast).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            }, ct);

            while (!ct.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(result.Buffer);
                if (text.Contains("Sonos", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("ZonePlayer", StringComparison.OrdinalIgnoreCase))
                {
                    lock (found)
                        found.Add(result.RemoteEndPoint.Address.ToString());
                }
            }

            await sender.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    /// <summary>Returns the IPv4 address of each up, multicast-capable, non-loopback interface.</summary>
    private static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            if (!nic.SupportsMulticast)
                continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                // Skip APIPA/link-local 169.254.x.x — those interfaces have no real LAN.
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;
                addresses.Add(ua.Address);
            }
        }
        return addresses;
    }
}
