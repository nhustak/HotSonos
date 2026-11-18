using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>What a RenderingControl NOTIFY changed. Mute and volume can arrive together.</summary>
public readonly record struct RenderingControlChange(bool VolumeChanged, bool MuteChanged, bool? Muted);

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
    private const string RenderingControlEventPath = "/MediaRenderer/RenderingControl/Event";
    private const string TopologyEventPath = "/ZoneGroupTopology/Event";

    private static readonly string[] EventPaths =
        [AvTransportEventPath, RenderingControlEventPath, TopologyEventPath];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Serializes NOTIFY handling so GENA floods cannot stack hundreds of concurrent handlers.</summary>
    private readonly SemaphoreSlim _callbackHandlerGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _inflightCallbacks;

    private TcpListener? _listener;
    private int _port;
    private string? _coordinatorIp;
    private readonly Dictionary<string, string> _sidByPath = []; // event path -> subscription id
    private CancellationTokenSource? _renewCts;
    private Task? _renewLoop;
    private bool _disposed;
    private string? _trustedCoordinatorIp;
    private string[] _trustedSids = [];

    /// <summary>Raised (background thread) when the coordinator pushes a track/state change.</summary>
    public event Action<NowPlaying>? NowPlayingChanged;

    /// <summary>
    /// Raised (background thread) when RenderingControl reports volume/mute on the
    /// subscribed player (usually the group coordinator). Does not include per-follower
    /// volumes — app should re-read all speakers on this signal.
    /// </summary>
    public event Action<RenderingControlChange>? VolumeChanged;

    /// <summary>Raised (background thread) with the decoded ZoneGroupState XML on a topology change.</summary>
    public event Action<string>? TopologyChanged;

    /// <summary>Drop current subscriptions and stop the renew loop. Poll-only until SubscribeAsync.</summary>
    public async Task UnsubscribeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ClearSubscriptionAsync(stopRenewLoop: true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

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
            {
                _sidByPath[path] = sid;
                PublishTrust();
            }
        }

        if (_sidByPath.Count > 0)
        {
            _coordinatorIp = coordinatorIp;
            PublishTrust();
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

                // ACK immediately so Sonos does not retry-storm while we process.
                var ok = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
                await stream.WriteAsync(ok, ct).ConfigureAwait(false);

                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
                if (!IsTrustedNotify(
                        request,
                        remoteIp,
                        Volatile.Read(ref _trustedCoordinatorIp),
                        Volatile.Read(ref _trustedSids)))
                    return;

                // Drop excess concurrency — topology floods were stacking handlers and killing the tray.
                if (Interlocked.Increment(ref _inflightCallbacks) > 8)
                {
                    Interlocked.Decrement(ref _inflightCallbacks);
                    return;
                }

                try
                {
                    await _callbackHandlerGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Route by body content: AVTransport / RenderingControl both use
                        // <LastChange>; topology uses <ZoneGroupState>.
                        if (request.Contains("<LastChange>", StringComparison.Ordinal))
                        {
                            var control = DescribeRenderingControl(request);
                            if (control.VolumeChanged || control.MuteChanged)
                            {
                                VolumeChanged?.Invoke(control);
                            }
                            else
                            {
                                var nowPlaying = ParseNotify(request, _coordinatorIp);
                                if (nowPlaying is not null)
                                    NowPlayingChanged?.Invoke(nowPlaying);
                            }
                        }
                        else if (request.Contains("<ZoneGroupState>", StringComparison.Ordinal))
                        {
                            var stateXml = ExtractTopology(request);
                            if (stateXml is not null)
                                TopologyChanged?.Invoke(stateXml);
                        }
                    }
                    finally
                    {
                        _callbackHandlerGate.Release();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _inflightCallbacks);
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

    /// <summary>
    /// Volume and mute from a RenderingControl LastChange. AVTransport bodies
    /// (transport state, track URI, metadata) return an empty change.
    /// </summary>
    internal static RenderingControlChange DescribeRenderingControl(string request)
    {
        var lastChangeMatch = LastChangeRegex().Match(request);
        if (!lastChangeMatch.Success)
            return default;
        var lastChange = WebUtility.HtmlDecode(lastChangeMatch.Groups[1].Value);
        if (lastChange.Contains("TransportState", StringComparison.OrdinalIgnoreCase)
            || lastChange.Contains("CurrentTrackURI", StringComparison.OrdinalIgnoreCase)
            || lastChange.Contains("AVTransportURI", StringComparison.OrdinalIgnoreCase)
            || lastChange.Contains("CurrentTrackMetaData", StringComparison.OrdinalIgnoreCase))
            return default;

        var volume = lastChange.Contains("<Volume", StringComparison.OrdinalIgnoreCase)
                     || lastChange.Contains("Volume val=", StringComparison.OrdinalIgnoreCase);
        var mute = MuteValRegex().Match(lastChange);
        var muteChanged = mute.Success
                          || lastChange.Contains("<Mute", StringComparison.OrdinalIgnoreCase)
                          || lastChange.Contains("Mute val=", StringComparison.OrdinalIgnoreCase);
        if (!volume && !muteChanged)
            return default;

        bool? muted = null;
        if (mute.Success)
        {
            var v = mute.Groups[1].Value.Trim();
            if (v is "1" or "true")
                muted = true;
            else if (v is "0" or "false")
                muted = false;
        }

        return new RenderingControlChange(volume, muteChanged, muted);
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

        int? currentTrack = null;
        int? numberOfTracks = null;
        if (int.TryParse(AttrVal(lastChange, "CurrentTrack"), out var ct) && ct > 0)
            currentTrack = ct;
        if (int.TryParse(AttrVal(lastChange, "NumberOfTracks"), out var nt) && nt > 0)
            numberOfTracks = nt;

        var position = NowPlaying.ParseSonosTime(AttrVal(lastChange, "RelTime"));
        var duration = NowPlaying.ParseSonosTime(AttrVal(lastChange, "CurrentTrackDuration"));

        var metaEscaped = AttrVal(lastChange, "CurrentTrackMetaData");
        if (string.IsNullOrEmpty(metaEscaped))
        {
            return new NowPlaying
            {
                State = state,
                TrackUri = trackUri,
                TransportStatus = transportStatus,
                CurrentTrack = currentTrack,
                NumberOfTracks = numberOfTracks,
                Position = position,
                Duration = duration,
            };
        }

        var meta = WebUtility.HtmlDecode(metaEscaped);
        var art = Tag(meta, "upnp:albumArtURI");
        // Sonos often sends relative /getaa?... — make absolute when we know the coordinator.
        if (!string.IsNullOrEmpty(art)
            && !art.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && coordinatorIp is not null)
        {
            if (art.StartsWith('/'))
                art = $"http://{coordinatorIp}:1400{art}";
            else
                art = $"http://{coordinatorIp}:1400/{art}";
        }
        else if (!string.IsNullOrEmpty(art)
                 && !art.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // No coordinator yet — drop relative art rather than throw on HttpClient.
            art = null;
        }

        return new NowPlaying
        {
            Title = Tag(meta, "dc:title"),
            Artist = Tag(meta, "upnp:artist") ?? Tag(meta, "dc:creator"),
            Album = Tag(meta, "upnp:album"),
            AlbumArtUri = string.IsNullOrEmpty(art) ? null : art,
            TrackUri = trackUri,
            TransportStatus = transportStatus,
            CurrentTrack = currentTrack,
            NumberOfTracks = numberOfTracks,
            State = state,
            Position = position,
            Duration = duration,
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
            PublishTrust();
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
        PublishTrust();
    }

    private void PublishTrust()
    {
        Volatile.Write(ref _trustedCoordinatorIp, _coordinatorIp);
        Volatile.Write(ref _trustedSids, _sidByPath.Values.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray());
    }

    /// <summary>
    /// A NOTIFY is accepted only when its SID is one we subscribed and the TCP
    /// sender is the coordinator we subscribed to. Anything else is ignored so a
    /// host on the LAN cannot invent play history.
    /// </summary>
    internal static bool IsTrustedNotify(
        string request, string? remoteIp, string? coordinatorIp, IReadOnlyList<string>? sids)
    {
        if (string.IsNullOrWhiteSpace(request) || sids is null || sids.Count == 0)
            return false;
        if (string.IsNullOrWhiteSpace(remoteIp) || string.IsNullOrWhiteSpace(coordinatorIp))
            return false;
        var headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
            headerEnd = request.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = headerEnd >= 0 ? request[..headerEnd] : request;
        var match = SidHeaderRegex().Match(headers);
        if (!match.Success)
            return false;
        var sid = match.Groups[1].Value.Trim();
        if (!sids.Any(s => string.Equals(s, sid, StringComparison.OrdinalIgnoreCase)))
            return false;
        return SameAddress(remoteIp, coordinatorIp);
    }

    private static bool SameAddress(string left, string right)
    {
        if (!IPAddress.TryParse(left.Trim(), out var a) || !IPAddress.TryParse(right.Trim(), out var b))
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        if (a.IsIPv4MappedToIPv6)
            a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6)
            b = b.MapToIPv4();
        return a.Equals(b);
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

                    var renewed = await RenewAsync(ct).ConfigureAwait(false);
                    if (renewed)
                        continue;

                    // Renew failed. Do NOT Clear+Subscribe from inside the renew loop while NOTIFY
                    // handlers may be mid-flight — that race hard-killed the tray process (~2.5–5 min
                    // after start = first/second renew). Wait for the next tick and try Renew again.
                    // Explicit SubscribeAsync (room change / refresh) still does a full re-subscribe.
                    System.Diagnostics.Debug.WriteLine(
                        $"[HotSonos GENA] renew failed for {_coordinatorIp}; will retry next cycle (no re-sub)");
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

    [GeneratedRegex(@"(?im)^SID:\s*(\S+)", RegexOptions.None)]
    private static partial Regex SidHeaderRegex();

    [GeneratedRegex(@"<Mute\b[^>]*\bval=""(?<v>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex MuteValRegex();

    [GeneratedRegex(@"<LastChange>(.*?)</LastChange>", RegexOptions.Singleline)]
    private static partial Regex LastChangeRegex();

    [GeneratedRegex(@"<ZoneGroupState>(.*?)</ZoneGroupState>", RegexOptions.Singleline)]
    private static partial Regex ZoneGroupStateRegex();
}
