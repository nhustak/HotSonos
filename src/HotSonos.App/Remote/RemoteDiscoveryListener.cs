using System.Net;
using System.Net.Sockets;
using System.Text;
using HotSonos.App.Infrastructure;
using HotSonos.Remote.Protocol;

namespace HotSonos.App.Remote;

/// <summary>
/// Answers phone probes on the same LAN address as the HTTP listener.
/// A reply is the address and the HTTP port. The PIN stays on the Phone window.
/// </summary>
sealed class RemoteDiscoveryListener : IAsyncDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly Task _loop;

    public RemoteDiscoveryListener(string ip, int httpPort)
    {
        _loop = Task.Run(() => LoopAsync(ip, httpPort, _cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Phone discovery loop ended", ex); }
        _cts.Dispose();
    }

    static async Task LoopAsync(string ip, int httpPort, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Parse(ip), RemoteDiscovery.Port));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Phone discovery could not bind {ip}:{RemoteDiscovery.Port}", ex);
            return;
        }

        AppLog.Info($"Phone discovery listening on {ip}:{RemoteDiscovery.Port}");
        var reply = Encoding.ASCII.GetBytes(RemoteDiscovery.BuildReply(ip, httpPort));
        var directed = RemoteDiscovery.DirectedBroadcast(ip);
        var nextAnnounce = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextAnnounce && directed is not null)
                {
                    await udp.SendAsync(reply, new IPEndPoint(directed, RemoteDiscovery.Port), ct).ConfigureAwait(false);
                    nextAnnounce = DateTime.UtcNow.AddSeconds(2);
                }

                if (udp.Available == 0)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    continue;
                }

                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(result.Buffer);
                if (!RemoteDiscovery.IsProbe(text))
                    continue;
                await udp.SendAsync(reply, result.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AppLog.Warn("Phone discovery packet failed", ex);
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
