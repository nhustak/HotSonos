using System.Net.Http;
using HotSonos.App.Infrastructure;

namespace HotSonos.App.Services;

/// <summary>
/// Periodically asks every known speaker "are you there?" and records the
/// transitions. Sonos topology reports membership, not liveness — a speaker that
/// has stopped answering keeps appearing in the group until its peers give up on
/// it, which is how a dead coordinator can look healthy while every command
/// times out. This watcher is the independent liveness check.
/// </summary>
public sealed class SpeakerReachabilityWatcher : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly SonosManager _sonos;
    private readonly SpeakerOutageLog _log;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Per-IP state: when it went down, and how many probes it has missed.</summary>
    private readonly Dictionary<string, (DateTimeOffset Since, int Misses)> _down =
        new(StringComparer.OrdinalIgnoreCase);

    private Task? _loop;

    public SpeakerReachabilityWatcher(SonosManager sonos, SpeakerOutageLog log, TimeSpan? interval = null)
    {
        _sonos = sonos;
        _log = log;
        _interval = interval ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>Rooms currently failing their probe, newest state.</summary>
    public IReadOnlyCollection<string> CurrentlyDownIps
    {
        get { lock (_down) return _down.Keys.ToList(); }
    }

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Let discovery settle before the first probe so startup is not noisy.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Speaker reachability probe failed", ex);
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ProbeOnceAsync(CancellationToken ct)
    {
        var topology = _sonos.LastTopology;
        if (topology is null || topology.Members.Count == 0)
            return;

        var targets = topology.Members
            .Where(m => !string.IsNullOrWhiteSpace(m.IpAddress))
            .GroupBy(m => m.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var results = await Task.WhenAll(targets.Select(async m =>
        {
            bool ok;
            try
            {
                using var resp = await Http.GetAsync(
                    $"http://{m.IpAddress}:1400/xml/device_description.xml",
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
                ok = resp.IsSuccessStatusCode;
            }
            catch
            {
                ok = false;
            }
            return (Member: m, Ok: ok);
        })).ConfigureAwait(false);

        var groupLabel = _sonos.ActiveGroupLabel;

        foreach (var (member, ok) in results)
        {
            lock (_down)
            {
                var wasDown = _down.TryGetValue(member.IpAddress, out var state);

                if (!ok)
                {
                    if (!wasDown)
                    {
                        _down[member.IpAddress] = (DateTimeOffset.Now, 1);
                        var ev = new SpeakerOutageEvent
                        {
                            Kind = "down",
                            Room = member.RoomName,
                            Ip = member.IpAddress,
                            IsCoordinator = member.IsCoordinator && !member.Invisible,
                            Group = groupLabel,
                        };
                        AppLog.Warn(ev.Describe());
                        _log.Record(ev);
                    }
                    else
                    {
                        _down[member.IpAddress] = (state.Since, state.Misses + 1);
                    }
                }
                else if (wasDown)
                {
                    _down.Remove(member.IpAddress);
                    var ev = new SpeakerOutageEvent
                    {
                        Kind = "up",
                        Room = member.RoomName,
                        Ip = member.IpAddress,
                        IsCoordinator = member.IsCoordinator && !member.Invisible,
                        Group = groupLabel,
                        DownSeconds = (DateTimeOffset.Now - state.Since).TotalSeconds,
                        MissedProbes = state.Misses,
                    };
                    AppLog.Info(ev.Describe());
                    _log.Record(ev);
                }
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
