using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>
/// UDP probe for the tray app. A miss is expected on an emulator (different subnet) and on
/// ChromeOS, where the Android container is NAT'd and UDP 42343 often never reaches the PC.
/// The typed address on Connect and Settings → Connection / PC is the way in.
/// </summary>
static class PcFinder
{
    public static async Task<(string Host, int Port)?> FindAsync(TimeSpan wait, CancellationToken ct)
    {
        using var hold = MulticastHold.Acquire();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var probe = Encoding.ASCII.GetBytes(RemoteDiscovery.Probe);
        foreach (var target in RemoteDiscovery.ProbeTargets(LocalIpv4()))
        {
            try
            {
                await udp.SendAsync(probe, new IPEndPoint(target, RemoteDiscovery.Port), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                // One interface can refuse a broadcast. The others may still answer.
            }
        }

        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (udp.Available == 0)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                continue;
            }

            var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            var text = Encoding.ASCII.GetString(result.Buffer);
            if (RemoteDiscovery.TryParseReply(text, out var host, out var port))
                return (host, port);
        }

        return null;
    }

    public static IReadOnlyList<string> LocalIpv4()
    {
        var list = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var bytes = address.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;
                list.Add(address.Address.ToString());
            }
        }

        return list;
    }
}
