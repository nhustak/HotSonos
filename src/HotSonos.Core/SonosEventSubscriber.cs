using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>
/// Subscribes to a coordinator's AVTransport GENA events and raises
/// <see cref="NowPlayingChanged"/> whenever the track or transport state
/// changes (push, not poll). Runs a small TCP HTTP server to receive the
/// speaker's NOTIFY callbacks, and renews the subscription before it expires.
/// </summary>
public sealed partial class SonosEventSubscriber : IAsyncDisposable
{
    private const int SubscriptionSeconds = 300;
    private const string EventPath = "/MediaRenderer/AVTransport/Event";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private int _port;
    private string? _coordinatorIp;
    private string? _sid;
    private Timer? _renewTimer;

    /// <summary>Raised (on a background thread) when the coordinator pushes a state change.</summary>
    public event Action<NowPlaying>? NowPlayingChanged;

    /// <summary>
    /// Points the subscription at <paramref name="coordinatorIp"/>. Starts the
    /// callback server on first use and re-subscribes if the coordinator changed.
    /// The speaker sends an immediate NOTIFY with current state on subscribe.
    /// </summary>
    public async Task SubscribeAsync(string coordinatorIp, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureListenerStarted();

            if (string.Equals(_coordinatorIp, coordinatorIp, StringComparison.OrdinalIgnoreCase) && _sid is not null)
                return; // already subscribed to this coordinator

            await UnsubscribeCurrentAsync().ConfigureAwait(false);

            var localIp = LocalIpFor(coordinatorIp);
            var callback = $"http://{localIp}:{_port}/notify";
            var sid = await SendSubscribeAsync(coordinatorIp, callback, ct).ConfigureAwait(false);
            if (sid is null)
                return;

            _coordinatorIp = coordinatorIp;
            _sid = sid;
            ScheduleRenew();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureListenerStarted()
    {
        if (_listener is not null)
            return;

        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = HandleCallbackAsync(client, ct);
        }
    }

    private async Task HandleCallbackAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);

                // Acknowledge so the speaker considers delivery successful.
                var ok = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
                await stream.WriteAsync(ok, ct).ConfigureAwait(false);

                var nowPlaying = ParseNotify(request, _coordinatorIp);
                if (nowPlaying is not null)
                    NowPlayingChanged?.Invoke(nowPlaying);
            }
        }
        catch
        {
            // A malformed/incomplete callback shouldn't take down the loop.
        }
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buffer = new byte[8192];
        var headerEnd = -1;
        var contentLength = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
                break;
            ms.Write(buffer, 0, read);

            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            if (headerEnd < 0)
            {
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    var m = ContentLengthRegex().Match(text[..headerEnd]);
                    if (m.Success)
                        contentLength = int.Parse(m.Groups[1].Value);
                }
            }

            if (headerEnd >= 0 && (int)ms.Length - (headerEnd + 4) >= contentLength)
                break;
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>Extracts now-playing state from a NOTIFY request body.</summary>
    internal static NowPlaying? ParseNotify(string request, string? coordinatorIp)
    {
        var lastChangeMatch = LastChangeRegex().Match(request);
        if (!lastChangeMatch.Success)
            return null;

        var lastChange = WebUtility.HtmlDecode(lastChangeMatch.Groups[1].Value);

        var state = SonosTransportStateParser.Parse(AttrVal(lastChange, "TransportState"));
        var metaEscaped = AttrVal(lastChange, "CurrentTrackMetaData");
        if (string.IsNullOrEmpty(metaEscaped))
            return new NowPlaying { State = state };

        var meta = WebUtility.HtmlDecode(metaEscaped);
        var art = Tag(meta, "upnp:albumArtURI");
        if (!string.IsNullOrEmpty(art) && art.StartsWith('/') && coordinatorIp is not null)
            art = $"http://{coordinatorIp}:1400{art}";

        return new NowPlaying
        {
            Title = Tag(meta, "dc:title"),
            Artist = Tag(meta, "upnp:artist") ?? Tag(meta, "dc:creator"),
            Album = Tag(meta, "upnp:album"),
            AlbumArtUri = string.IsNullOrEmpty(art) ? null : art,
            State = state,
        };
    }

    private static string? AttrVal(string xml, string element)
    {
        var m = Regex.Match(xml, $"<{element} val=\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Tag(string xml, string tag)
    {
        var m = Regex.Match(xml, $"<{Regex.Escape(tag)}[^>]*>([^<]*)</{Regex.Escape(tag)}>");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    // ---- subscription lifecycle -------------------------------------------

    private async Task<string?> SendSubscribeAsync(string coordinatorIp, string callbackUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), $"http://{coordinatorIp}:1400{EventPath}");
        req.Headers.TryAddWithoutValidation("CALLBACK", $"<{callbackUrl}>");
        req.Headers.TryAddWithoutValidation("NT", "upnp:event");
        req.Headers.TryAddWithoutValidation("TIMEOUT", $"Second-{SubscriptionSeconds}");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        return resp.Headers.TryGetValues("SID", out var sids) ? sids.FirstOrDefault() : null;
    }

    private async Task<bool> RenewAsync(CancellationToken ct)
    {
        if (_coordinatorIp is null || _sid is null)
            return false;

        using var req = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), $"http://{_coordinatorIp}:1400{EventPath}");
        req.Headers.TryAddWithoutValidation("SID", _sid);
        req.Headers.TryAddWithoutValidation("TIMEOUT", $"Second-{SubscriptionSeconds}");
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task UnsubscribeCurrentAsync()
    {
        _renewTimer?.Dispose();
        _renewTimer = null;

        if (_coordinatorIp is null || _sid is null)
            return;

        try
        {
            using var req = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), $"http://{_coordinatorIp}:1400{EventPath}");
            req.Headers.TryAddWithoutValidation("SID", _sid);
            using var _ = await _http.SendAsync(req).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort; the subscription will lapse on its own otherwise.
        }
        finally
        {
            _sid = null;
            _coordinatorIp = null;
        }
    }

    private void ScheduleRenew()
    {
        _renewTimer?.Dispose();
        var half = TimeSpan.FromSeconds(SubscriptionSeconds / 2.0);
        _renewTimer = new Timer(async _ =>
        {
            if (!await RenewAsync(_cts.Token).ConfigureAwait(false))
            {
                // Renewal failed — try a fresh subscription to the same coordinator.
                var ip = _coordinatorIp;
                if (ip is not null)
                {
                    _sid = null;
                    await SubscribeAsync(ip).ConfigureAwait(false);
                }
            }
        }, null, half, half);
    }

    private static IPAddress LocalIpFor(string coordinatorIp)
    {
        // Connecting a UDP socket picks the local interface that routes to the
        // speaker — the address the speaker can call back on.
        using var udp = new UdpClient();
        udp.Connect(coordinatorIp, 1400);
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Address;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await UnsubscribeCurrentAsync().ConfigureAwait(false);
        _listener?.Stop();
        _http.Dispose();
        _gate.Dispose();
        _cts.Dispose();
    }

    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLengthRegex();

    [GeneratedRegex(@"<LastChange>(.*?)</LastChange>", RegexOptions.Singleline)]
    private static partial Regex LastChangeRegex();
}
