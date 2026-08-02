using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using HotSonos.App.Infrastructure;
using HotSonos.App.Library;
using HotSonos.App.Models;
using HotSonos.Core.Models;

namespace HotSonos.App.Services;

/// <summary>
/// One-shot "something just failed" diagnostic: LAN pings, NAS/SMB, Sonos speakers (ICMP + :1400),
/// topology/now-playing snapshot, app state. Writes a report file and returns the text.
/// </summary>
public sealed class FailureDiagnosticService
{
    private readonly SonosManager _sonos;
    private readonly Func<AppSettings> _settings;
    private readonly Func<NowPlaying?> _lastNowPlaying;
    private readonly LibraryService? _library;
    private readonly Func<bool> _mcpRunning;
    private readonly Func<string?> _mcpEndpoint;

    public FailureDiagnosticService(
        SonosManager sonos,
        Func<AppSettings> settings,
        Func<NowPlaying?> lastNowPlaying,
        LibraryService? library,
        Func<bool> mcpRunning,
        Func<string?> mcpEndpoint)
    {
        _sonos = sonos;
        _settings = settings;
        _lastNowPlaying = lastNowPlaying;
        _library = library;
        _mcpRunning = mcpRunning;
        _mcpEndpoint = mcpEndpoint;
    }

    public static string ReportsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotSonos", "diagnostics");

    /// <summary>Run all probes. Safe to call off the UI thread.</summary>
    public async Task<FailureDiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder(16_384);
        var failHints = new List<string>();
        var s = _settings().EnsureShape();

        sb.AppendLine("=== HotSonos FAILURE DIAGNOSTIC ===");
        sb.AppendLine($"utc={DateTime.UtcNow:O}");
        sb.AppendLine($"local={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"version={AppVersion.Current}");
        sb.AppendLine($"pid={Environment.ProcessId}");
        sb.AppendLine($"machine={Environment.MachineName}");
        sb.AppendLine($"user={Environment.UserName}");
        sb.AppendLine($"gena={SonosManager.UseGenaSubscriptions} poll={SonosManager.UseNowPlayingPoll}");
        sb.AppendLine($"mcpRunning={_mcpRunning()} endpoint={_mcpEndpoint() ?? "(none)"}");
        sb.AppendLine();

        // ---- PC network adapters / gateway ----
        sb.AppendLine("--- Local adapters ---");
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                                         and not NetworkInterfaceType.Tunnel))
            {
                var props = nic.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToList();
                var gw = props.GatewayAddresses
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(g => g.Address.ToString())
                    .ToList();
                sb.AppendLine(
                    $"  {nic.Name} | {nic.NetworkInterfaceType} | {nic.Speed / 1_000_000}Mbps | " +
                    $"ip=[{string.Join(", ", ipv4)}] gw=[{string.Join(", ", gw)}]");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  ERROR listing adapters: {ex.Message}");
            failHints.Add("adapter-list-failed");
        }

        sb.AppendLine();
        sb.AppendLine("--- Ping core targets ---");
        var gateways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                foreach (var g in nic.GetIPProperties().GatewayAddresses)
                {
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(g.Address)
                        && !g.Address.Equals(IPAddress.Any))
                        gateways.Add(g.Address.ToString());
                }
            }
        }
        catch { /* ignore */ }

        var coreTargets = new List<(string Label, string Host)>
        {
            ("cloudflare-dns", "1.1.1.1"),
            ("google-dns", "8.8.8.8"),
        };
        foreach (var gw in gateways.OrderBy(x => x))
            coreTargets.Insert(0, ($"gateway", gw));

        // NAS / music host from library UNC paths + common stormnas
        var nasHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in s.SonosLibraryRoots.Concat(s.DailyLibraryRoots))
        {
            var host = TryHostFromUnc(root);
            if (host is not null) nasHosts.Add(host);
        }
        if (!string.IsNullOrWhiteSpace(s.MasterLibraryRoot))
        {
            var host = TryHostFromUnc(s.MasterLibraryRoot);
            if (host is not null) nasHosts.Add(host);
        }
        nasHosts.Add("192.168.1.111");
        nasHosts.Add("stormnas");
        nasHosts.Add("storm");

        foreach (var host in nasHosts.OrderBy(h => h))
            coreTargets.Add(($"nas:{host}", host));

        foreach (var (label, host) in coreTargets)
        {
            ct.ThrowIfCancellationRequested();
            var r = await PingHostAsync(host, timeoutMs: 1500, count: 3, ct).ConfigureAwait(false);
            sb.AppendLine($"  [{label}] {r.Summary}");
            if (!r.Ok) failHints.Add($"ping-fail:{label}");
        }

        // ---- SMB / library roots ----
        sb.AppendLine();
        sb.AppendLine("--- Library SMB / path reachability ---");
        var roots = s.SonosLibraryRoots
            .Concat(s.DailyLibraryRoots)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        if (roots.Count == 0)
            sb.AppendLine("  (no library roots configured)");
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await ProbePathAsync(root, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            sb.AppendLine($"  {probe}");
            if (probe.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
                || probe.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase))
                failHints.Add($"smb:{root}");
        }

        // ---- Sonos topology (cached) + live refresh attempt ----
        sb.AppendLine();
        sb.AppendLine("--- Sonos topology (before refresh) ---");
        AppendTopology(sb, _sonos);
        if (_sonos.OfflineSpeakers.Count > 0)
            failHints.Add($"offline-speakers:{_sonos.OfflineSpeakers.Count}");

        sb.AppendLine();
        sb.AppendLine("--- Sonos refresh (live discovery ~4s) ---");
        try
        {
            var refreshSw = Stopwatch.StartNew();
            await _sonos.RefreshAsync(s.ActiveRoom, ct).ConfigureAwait(false);
            sb.AppendLine($"  refresh OK in {refreshSw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  refresh FAIL: {ex.GetType().Name}: {ex.Message}");
            failHints.Add("sonos-refresh-failed");
        }

        sb.AppendLine();
        sb.AppendLine("--- Sonos topology (after refresh) ---");
        AppendTopology(sb, _sonos);

        // ---- Ping / TCP 1400 every zone ----
        sb.AppendLine();
        sb.AppendLine("--- Sonos zones ICMP + TCP:1400 ---");
        var zones = _sonos.GetZoneEndpoints();
        if (zones.Count == 0)
        {
            sb.AppendLine("  (no zones known — discovery empty)");
            failHints.Add("no-zones");
        }
        else
        {
            // Parallel but bounded
            var tasks = zones.Select(async z =>
            {
                var ping = await PingHostAsync(z.Ip, timeoutMs: 1200, count: 2, ct).ConfigureAwait(false);
                var tcp = await TcpProbeAsync(z.Ip, 1400, timeoutMs: 1500, ct).ConfigureAwait(false);
                return (z.Room, z.Ip, ping, tcp);
            }).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var r in results.OrderBy(x => x.Room, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"  {r.Room} @ {r.Ip} | icmp={r.ping.Short} | tcp1400={r.tcp}");
                if (!r.ping.Ok) failHints.Add($"zone-icmp:{r.Room}");
                if (!r.tcp.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                    failHints.Add($"zone-tcp1400:{r.Room}");
            }
        }

        // ---- Now playing / transport ----
        sb.AppendLine();
        sb.AppendLine("--- Now playing / transport ---");
        var cached = _lastNowPlaying();
        sb.AppendLine($"  cached: {FormatNp(cached)}");
        try
        {
            var live = await _sonos.FetchNowPlayingAsync(ct).ConfigureAwait(false);
            sb.AppendLine($"  live:   {FormatNp(live)}");
            if (live is not null
                && !string.IsNullOrWhiteSpace(live.TransportStatus)
                && live.TransportStatus.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                failHints.Add($"transport-status:{live.TransportStatus}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  live fetch FAIL: {ex.Message}");
            failHints.Add("now-playing-fetch-failed");
        }

        try
        {
            var playing = await _sonos.IsAnythingPlayingAsync(ct).ConfigureAwait(false);
            sb.AppendLine($"  IsAnythingPlaying={playing}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  IsAnythingPlaying FAIL: {ex.Message}");
        }

        sb.AppendLine($"  playback: {System.Text.Json.JsonSerializer.Serialize(_sonos.GetPlaybackSessionSnapshot())}");

        // ---- Library cache ----
        sb.AppendLine();
        sb.AppendLine("--- Library cache ---");
        try
        {
            if (_library is null)
                sb.AppendLine("  (library service null)");
            else
            {
                var st = _library.GetStatus();
                sb.AppendLine(
                    $"  tracks={st.TrackCount} scanning={st.IsScanning} " +
                    $"lastFinished={st.LastScanFinishedUtc?.ToString("o") ?? "(never)"} " +
                    $"lastError={st.LastScanError ?? "-"}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  library status FAIL: {ex.Message}");
        }

        // ---- Recent app log (last 40 lines) ----
        sb.AppendLine();
        sb.AppendLine("--- Recent app log (tail) ---");
        try
        {
            var recent = AppLog.GetRecentText(40);
            foreach (var line in recent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                sb.AppendLine($"  {line}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  log tail FAIL: {ex.Message}");
        }

        // ---- Summary ----
        sw.Stop();
        sb.AppendLine();
        sb.AppendLine("--- SUMMARY ---");
        sb.AppendLine($"elapsedMs={sw.ElapsedMilliseconds}");
        if (failHints.Count == 0)
        {
            sb.AppendLine("result=OK (no hard failures flagged)");
            sb.AppendLine(
                "note=If audio still cut out, it may be Sonos mesh / group stall without ICMP loss. " +
                "Re-run while glitching if possible.");
        }
        else
        {
            sb.AppendLine($"result=ISSUES ({failHints.Count} flag(s))");
            foreach (var h in failHints.Distinct(StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  - {h}");
        }

        var text = sb.ToString();
        string? path = null;
        try
        {
            Directory.CreateDirectory(ReportsDirectory);
            path = Path.Combine(
                ReportsDirectory,
                $"failure-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not write failure diagnostic file", ex);
        }

        AppLog.Info(
            failHints.Count == 0
                ? $"Failure diagnostic OK ({sw.ElapsedMilliseconds}ms) → {path ?? "(no file)"}"
                : $"Failure diagnostic ISSUES x{failHints.Count} ({sw.ElapsedMilliseconds}ms) → {path ?? "(no file)"}");
        AppLog.Lifecycle(
            $"FailureDiagnostic flags={failHints.Count} ms={sw.ElapsedMilliseconds} file={path ?? "-"}");

        return new FailureDiagnosticResult(
            text,
            path,
            failHints.Count,
            sw.ElapsedMilliseconds);
    }

    private static void AppendTopology(StringBuilder sb, SonosManager sonos)
    {
        sb.AppendLine($"  activeRoom={sonos.ActiveRoom ?? "(null)"} groupLabel={sonos.ActiveGroupLabel}");
        sb.AppendLine($"  groups={sonos.Groups.Count} zones={sonos.GetZoneCount()} offline=[{string.Join(", ", sonos.OfflineSpeakers)}]");
        foreach (var g in sonos.Groups)
            sb.AppendLine($"  group: {g.DisplayName} coord={g.CoordinatorRoom} @{g.CoordinatorIp} members={g.MemberCount}");
        foreach (var z in sonos.GetZoneEndpoints().OrderBy(z => z.Room, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"  zone: {z.Room} @{z.Ip}");
    }

    private static string FormatNp(NowPlaying? np)
    {
        if (np is null || np.IsEmpty) return "(empty)";
        return $"state={np.State} track={np.CurrentTrack}/{np.NumberOfTracks} " +
               $"status={np.TransportStatus ?? "-"} | {np.DisplayLine} | uri={np.TrackUri}";
    }

    private static string? TryHostFromUnc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var rest = path[2..];
            var slash = rest.IndexOfAny(['\\', '/']);
            return slash < 0 ? rest : rest[..slash];
        }
        // x-file-cifs://host/...
        if (path.Contains("://", StringComparison.Ordinal))
        {
            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
                    return u.Host;
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private static async Task<PingResult> PingHostAsync(
        string host, int timeoutMs, int count, CancellationToken ct)
    {
        try
        {
            // Resolve first so we can show IP
            IPAddress[] addrs;
            try
            {
                addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new PingResult(false, $"{host} DNS-FAIL: {ex.Message}", "DNS-FAIL");
            }

            var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? addrs.FirstOrDefault();
            if (ip is null)
                return new PingResult(false, $"{host} DNS-FAIL: no addresses", "DNS-FAIL");

            using var ping = new Ping();
            var times = new List<long>();
            var lost = 0;
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var reply = await ping.SendPingAsync(ip, timeoutMs).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                        times.Add(reply.RoundtripTime);
                    else
                        lost++;
                }
                catch
                {
                    lost++;
                }
            }

            var ok = times.Count > 0;
            var avg = times.Count > 0 ? times.Average() : -1;
            var summary =
                $"{host} ({ip}) ok={times.Count}/{count} loss={lost} avgMs={(ok ? avg.ToString("0") : "-")}";
            var shorty = ok ? $"OK {avg:0}ms loss={lost}/{count}" : $"FAIL loss={lost}/{count}";
            return new PingResult(ok, summary, shorty);
        }
        catch (Exception ex)
        {
            return new PingResult(false, $"{host} ERROR: {ex.Message}", "ERROR");
        }
    }

    private static async Task<string> TcpProbeAsync(
        string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var reg = ct.Register(() =>
            {
                try { client.Close(); } catch { /* ignore */ }
            });
            var connect = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
            if (done != connect)
                return "TIMEOUT";
            await connect.ConfigureAwait(false);
            return client.Connected ? "OK" : "FAIL";
        }
        catch (Exception ex)
        {
            return $"FAIL:{ex.GetType().Name}";
        }
    }

    private static async Task<string> ProbePathAsync(
        string path, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var probe = Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        // cheap listing to prove SMB not just mount stub
                        _ = Directory.EnumerateFileSystemEntries(path).Take(3).ToList();
                        return $"OK  exists+list  {path}";
                    }
                    if (File.Exists(path))
                        return $"OK  file exists  {path}";
                    return $"FAIL not found  {path}";
                }
                catch (Exception ex)
                {
                    return $"FAIL {ex.GetType().Name}: {ex.Message}  {path}";
                }
            }, ct);

            var done = await Task.WhenAny(probe, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (done != probe)
                return $"TIMEOUT after {timeout.TotalSeconds:0}s  {path}";
            return await probe.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"FAIL {ex.Message}  {path}";
        }
    }

    private readonly record struct PingResult(bool Ok, string Summary, string Short);
}

public sealed record FailureDiagnosticResult(
    string ReportText,
    string? ReportPath,
    int IssueCount,
    long ElapsedMs);
