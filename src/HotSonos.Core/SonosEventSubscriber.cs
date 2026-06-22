using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>
/// Subscribes to a coordinator's AVTransport and ZoneGroupTopology GENA events
/// and raises <see cref="NowPlayingChanged"/> / <see cref="TopologyChanged"/>
/// on push (no polling). Runs a small TCP HTTP server for the NOTIFY callbacks
/// and renews both subscriptions before they expire.
/// </summary>
public sealed partial class SonosEventSubscriber : IAsyncDisposable
{
    private const int SubscriptionSeconds = 300;
    private const string AvTransportEventPath = "/MediaRenderer/AVTransport/Event";
    private const string TopologyEventPath = "/ZoneGroupTopology/Event";

    private static readonly string[] EventPaths = [AvTransportEventPath, TopologyEventPath];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private int _port;
    private string? _coordinatorIp;
    private readonly Dictionary<string, string> _sidByPath = []; // event path -> subscription id
    private Timer? _renewTimer;

    /// <summary>Raised (background thread) when the coordinator pushes a track/state change.</summary>
    public event Action<NowPlaying>? NowPlayingChanged;

    /// <summary>Raised (background thread) with the decoded ZoneGroupState XML on a topology change.</summary>
    public event Action<string>? TopologyChanged;

    /// <summary>
    /// Points the subscriptions at <paramref name="coordinatorIp"/>. Starts the
    /// callback server on first use and re-subscribes if the coordinator changed.
    /// The speaker sends an immediate NOTIFY with current state on subscribe.
    /// </summary>
    public async Task SubscribeAsync(string coordinatorIp, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureListenerStarted();

            if (string.Equals(_coordinatorIp, coordinatorIp, StringComparison.OrdinalIgnoreCase) && _sidByPath.Count > 0)
                return; // already subscribed to this coordinator

            await UnsubscribeAllAsync().ConfigureAwait(false);

            var localIp = LocalIpFor(coordinatorIp);
            var callback = $"http://{localIp}:{_port}/notify";
            foreach (var path in EventPaths)
            {
                var sid = await SendSubscribeAsync(coordinatorIp, path, callback, ct).ConfigureAwait(false);
                if (sid is not null)
                    _sidByPath[path] = sid;
            }

            if (_sidByPath.Count > 0)
            {
                _coordinatorIp = coordinatorIp;
                ScheduleRenew();
            }
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

                var ok = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
                await stream.WriteAsync(ok, ct).ConfigureAwait(false);

                // Route by body content: AVTransport carries <LastChange>, topology <ZoneGroupState>.
                if (request.Contains("<LastChange>", StringComparison.Ordinal))
                {
                    var nowPlaying = ParseNotify(request, _coordinatorIp);
                    if (nowPlaying is not null)
                        NowPlayingChanged?.Invoke(nowPlaying);
                }
                else if (request.Contains("<ZoneGroupState>", StringComparison.Ordinal))
                {
                    var stateXml = ExtractTopology(request);
                    if (stateXml is not null)
                        TopologyChanged?.Invoke(stateXml);
                }
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

    /// <summary>Extracts now-playing state from an AVTransport NOTIFY body.</summary>
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

    /// <summary>Pulls the (decoded) inner ZoneGroupState XML out of a topology NOTIFY body.</summary>
    internal static string? ExtractTopology(string request)
    {
        var m = ZoneGroupStateRegex().Match(request);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
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

    private async Task<string?> SendSubscribeAsync(string coordinatorIp, string path, string callbackUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), $"http://{coordinatorIp}:1400{path}");
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
        if (_coordinatorIp is null || _sidByPath.Count == 0)
            return false;

        var allOk = true;
        foreach (var (path, sid) in _sidByPath)
        {
            using var req = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), $"http://{_coordinatorIp}:1400{path}");
            req.Headers.TryAddWithoutValidation("SID", sid);
            req.Headers.TryAddWithoutValidation("TIMEOUT", $"Second-{SubscriptionSeconds}");
            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                allOk &= resp.IsSuccessStatusCode;
            }
            catch
            {
                allOk = false;
            }
        }
        return allOk;
    }

    private async Task UnsubscribeAllAsync()
    {
        _renewTimer?.Dispose();
        _renewTimer = null;

        if (_coordinatorIp is null)
        {
            _sidByPath.Clear();
            return;
        }

        foreach (var (path, sid) in _sidByPath)
        {
            try
            {
                using var req = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), $"http://{_coordinatorIp}:1400{path}");
                req.Headers.TryAddWithoutValidation("SID", sid);
                using var _ = await _http.SendAsync(req).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort; the subscription lapses on its own otherwise.
            }
        }

        _sidByPath.Clear();
        _coordinatorIp = null;
    }

    private void ScheduleRenew()
    {
        _renewTimer?.Dispose();
        var half = TimeSpan.FromSeconds(SubscriptionSeconds / 2.0);
        _renewTimer = new Timer(async _ =>
        {
            if (!await RenewAsync(_cts.Token).ConfigureAwait(false))
            {
                var ip = _coordinatorIp;
                if (ip is not null)
                {
                    await UnsubscribeAllAsync().ConfigureAwait(false);
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
        await UnsubscribeAllAsync().ConfigureAwait(false);
        _listener?.Stop();
        _http.Dispose();
        _gate.Dispose();
        _cts.Dispose();
    }

    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLengthRegex();

    [GeneratedRegex(@"<LastChange>(.*?)</LastChange>", RegexOptions.Singleline)]
    private static partial Regex LastChangeRegex();

    [GeneratedRegex(@"<ZoneGroupState>(.*?)</ZoneGroupState>", RegexOptions.Singleline)]
    private static partial Regex ZoneGroupStateRegex();
}
