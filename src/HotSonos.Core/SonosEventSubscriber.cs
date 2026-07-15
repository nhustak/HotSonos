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
    private readonly CancellationTokenSource _lifetimeCts = new();

    private TcpListener? _listener;
    private int _port;
    private string? _coordinatorIp;
    private readonly Dictionary<string, string> _sidByPath = []; // event path -> subscription id
    private CancellationTokenSource? _renewCts;
    private Task? _renewLoop;
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await SubscribeCoreAsync(coordinatorIp, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Must be called while holding <see cref="_gate"/>.
    /// When <paramref name="manageRenewLoop"/> is false, the caller already owns
    /// the renew loop (used from inside the loop after a failed renew) so we must
    /// not stop/restart it — that would await the current task and deadlock.
    /// </summary>
    private async Task SubscribeCoreAsync(string coordinatorIp, CancellationToken ct, bool manageRenewLoop = true)
    {
        EnsureListenerStarted();

        if (string.Equals(_coordinatorIp, coordinatorIp, StringComparison.OrdinalIgnoreCase) && _sidByPath.Count > 0)
            return; // already subscribed to this coordinator

        await ClearSubscriptionAsync(stopRenewLoop: manageRenewLoop).ConfigureAwait(false);

        var localIp = LocalIpFor(coordinatorIp);
        var callback = $"http://{localIp}:{_port}/notify";
        foreach (var path in EventPaths)
        {
            ct.ThrowIfCancellationRequested();
            var sid = await SendSubscribeAsync(coordinatorIp, path, callback, ct).ConfigureAwait(false);
            if (sid is not null)
                _sidByPath[path] = sid;
        }

        if (_sidByPath.Count > 0)
        {
            _coordinatorIp = coordinatorIp;
            if (manageRenewLoop)
                StartRenewLoop();
        }
    }

    private void EnsureListenerStarted()
    {
        if (_listener is not null)
            return;

        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_lifetimeCts.Token);
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
        var transportStatus = AttrVal(lastChange, "TransportStatus");
        var trackUri = AttrVal(lastChange, "CurrentTrackURI")
            ?? AttrVal(lastChange, "AVTransportURI");
        if (!string.IsNullOrEmpty(trackUri))
            trackUri = WebUtility.HtmlDecode(trackUri);

        var metaEscaped = AttrVal(lastChange, "CurrentTrackMetaData");
        if (string.IsNullOrEmpty(metaEscaped))
            return new NowPlaying { State = state, TrackUri = trackUri, TransportStatus = transportStatus };

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
            TrackUri = trackUri,
            TransportStatus = transportStatus,
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

        // Snapshot so we don't mutate while enumerating if something else races.
        var snapshot = _sidByPath.ToArray();
        var allOk = true;
        foreach (var (path, sid) in snapshot)
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

    /// <summary>Must be called while holding <see cref="_gate"/> (or during dispose after cancel).</summary>
    private async Task ClearSubscriptionAsync(bool stopRenewLoop)
    {
        if (stopRenewLoop)
            await StopRenewLoopAsync().ConfigureAwait(false);

        if (_coordinatorIp is null)
        {
            _sidByPath.Clear();
            return;
        }

        foreach (var (path, sid) in _sidByPath.ToArray())
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

    /// <summary>
    /// Starts a cancellable renew loop. Prefer this over <see cref="Timer"/> + async
    /// callbacks: dispose can cancel cleanly and work always runs under <see cref="_gate"/>.
    /// </summary>
    private void StartRenewLoop()
    {
        // Caller holds _gate; prior loop was stopped by ClearSubscriptionAsync(stopRenewLoop: true).
        _renewCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var ct = _renewCts.Token;
        _renewLoop = RenewLoopAsync(ct);
    }

    private async Task RenewLoopAsync(CancellationToken ct)
    {
        var half = TimeSpan.FromSeconds(SubscriptionSeconds / 2.0);
        try
        {
            using var timer = new PeriodicTimer(half);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_disposed || _coordinatorIp is null || _sidByPath.Count == 0)
                        continue;

                    if (await RenewAsync(ct).ConfigureAwait(false))
                        continue;

                    // Renew failed — re-subscribe without stopping this loop (manageRenewLoop: false).
                    var ip = _coordinatorIp;
                    await ClearSubscriptionAsync(stopRenewLoop: false).ConfigureAwait(false);
                    if (!_disposed && ip is not null && !ct.IsCancellationRequested)
                        await SubscribeCoreAsync(ip, ct, manageRenewLoop: false).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose or coordinator change.
        }
        catch
        {
            // Keep the process alive; a later SubscribeAsync or restart recovers.
        }
    }

    private async Task StopRenewLoopAsync()
    {
        var cts = _renewCts;
        var loop = _renewLoop;
        _renewCts = null;
        _renewLoop = null;

        if (cts is not null)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); } catch { /* already disposed */ }
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* already observed in the loop */ }
        }

        cts?.Dispose();
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
        if (_disposed)
            return;
        _disposed = true;

        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        // Stop renew first (may be mid-tick waiting on gate), then unsubscribe.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ClearSubscriptionAsync(stopRenewLoop: true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        try { _listener?.Stop(); } catch { /* ignore */ }
        _http.Dispose();
        _gate.Dispose();
        _lifetimeCts.Dispose();
    }

    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLengthRegex();

    [GeneratedRegex(@"<LastChange>(.*?)</LastChange>", RegexOptions.Singleline)]
    private static partial Regex LastChangeRegex();

    [GeneratedRegex(@"<ZoneGroupState>(.*?)</ZoneGroupState>", RegexOptions.Singleline)]
    private static partial Regex ZoneGroupStateRegex();
}
