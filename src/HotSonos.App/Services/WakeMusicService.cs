using HotSonos.App.Infrastructure;
using HotSonos.App.Models;
using HotSonos.Core;

namespace HotSonos.App.Services;

/// <summary>
/// Schedules wake-to-music: start on one room at low volume, step volume up,
/// optionally expand to whole-house full-library shuffle when the ramp finishes.
/// </summary>
public sealed class WakeMusicService : IDisposable
{
    private readonly SonosManager _sonos;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string> _status;
    private readonly Action _stateChanged;
    private readonly SemaphoreSlim _actionGate;

    private System.Threading.Timer? _scheduleTimer;
    private CancellationTokenSource? _wakeCts;
    private int _active; // 0 idle, 1 running wake (ramp or expand)

    public WakeMusicService(
        SonosManager sonos,
        Func<AppSettings> settings,
        Action<string> status,
        Action stateChanged,
        SemaphoreSlim actionGate)
    {
        _sonos = sonos;
        _settings = settings;
        _status = status;
        _stateChanged = stateChanged;
        _actionGate = actionGate;
    }

    /// <summary>True while a wake ramp or house expand is in progress.</summary>
    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>Arms the next one-shot fire from current settings (or clears if disabled).</summary>
    public void Schedule()
    {
        _scheduleTimer?.Dispose();
        _scheduleTimer = null;

        var s = _settings().EnsureShape();
        if (!s.WakeEnabled)
        {
            AppLog.Info("Wake to music: disabled");
            return;
        }

        if (s.WakeDaysMask == 0)
        {
            AppLog.Warn("Wake to music: enabled but no days selected — will not fire");
            return;
        }

        var next = ComputeNextFire(DateTime.Now, s.WakeMinutes, s.WakeDaysMask);
        if (next is null)
        {
            AppLog.Warn("Wake to music: could not compute next fire time");
            return;
        }

        var delay = next.Value - DateTime.Now;
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        AppLog.Info($"Wake to music scheduled for {next.Value:yyyy-MM-dd HH:mm} (room={s.WakeRoom ?? "active"}, source={s.WakeSource}, expand={s.WakeExpandToHouse})");
        _scheduleTimer = new System.Threading.Timer(_ => _ = OnScheduleFiredAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Cancels an in-progress ramp/expand (does not stop Sonos playback).</summary>
    public void Cancel()
    {
        try { _wakeCts?.Cancel(); } catch { /* ignore */ }
        AppLog.Info("Wake to music cancelled by user");
        _status("Wake cancelled");
    }

    /// <summary>Fire wake immediately (MCP / tests). Still skips if music is already playing.</summary>
    public Task TriggerNowAsync() => RunWakeAsync();

    /// <summary>Next scheduled local fire time, or null if disabled / no days.</summary>
    public DateTime? GetNextFireLocal()
    {
        var s = _settings().EnsureShape();
        if (!s.WakeEnabled || s.WakeDaysMask == 0)
            return null;
        return ComputeNextFire(DateTime.Now, s.WakeMinutes, s.WakeDaysMask);
    }

    private async Task OnScheduleFiredAsync()
    {
        try
        {
            await RunWakeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Wake to music cancelled");
        }
        catch (Exception ex)
        {
            AppLog.Error("Wake to music failed", ex);
            _status($"Wake error: {ex.Message}");
        }
        finally
        {
            // Re-arm for the next matching day.
            try
            {
                // Marshal schedule update; Schedule is thread-safe enough (disposes prior timer).
                Schedule();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Wake re-schedule failed", ex);
            }
        }
    }

    private async Task RunWakeAsync()
    {
        var settings = _settings().EnsureShape();
        if (!settings.WakeEnabled)
            return;
        if (!settings.WakeIncludesDay(DateTime.Now.DayOfWeek))
        {
            AppLog.Info("Wake to music: skipped (day not selected)");
            return;
        }

        // Refresh topology, then bail if anything is already playing (anywhere).
        try
        {
            await _sonos.RefreshAsync(_sonos.ActiveRoom).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Wake pre-check refresh failed; continuing with cached topology", ex);
        }

        if (await _sonos.IsAnythingPlayingAsync().ConfigureAwait(false))
        {
            AppLog.Info("Wake to music: skipped — Sonos is already playing");
            return;
        }

        CancelPreviousWakeCts();
        var cts = new CancellationTokenSource();
        _wakeCts = cts;
        var ct = cts.Token;

        Interlocked.Exchange(ref _active, 1);
        _stateChanged();

        try
        {
            // Re-check after acquiring the gate in case something started just now.
            if (await _sonos.IsAnythingPlayingAsync(ct).ConfigureAwait(false))
            {
                AppLog.Info("Wake to music: skipped — Sonos started playing before wake could begin");
                return;
            }

            await _actionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await _sonos.IsAnythingPlayingAsync(ct).ConfigureAwait(false))
                {
                    AppLog.Info("Wake to music: skipped — Sonos is already playing");
                    return;
                }

                await ExecutePhaseAAsync(settings, ct).ConfigureAwait(false);
            }
            finally
            {
                _actionGate.Release();
            }

            // Ramp outside the exclusive gate so the user can still use short hotkeys
            // (those cancel the wake). Expand re-takes the gate for shuffle.
            var finishedNaturally = await RampAsync(settings, ct).ConfigureAwait(false);
            if (finishedNaturally && settings.WakeExpandToHouse)
            {
                await _actionGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await ExecutePhaseBAsync(settings, ct).ConfigureAwait(false);
                }
                finally
                {
                    _actionGate.Release();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
            _stateChanged();
            if (ReferenceEquals(_wakeCts, cts))
                _wakeCts = null;
            cts.Dispose();
        }
    }

    private async Task ExecutePhaseAAsync(AppSettings settings, CancellationToken ct)
    {
        // Topology was refreshed in RunWakeAsync; keep ActiveRoom for hotkeys.
        var room = settings.WakeRoom;
        var group = _sonos.TryGetGroup(room) ?? _sonos.TryGetGroup(_sonos.ActiveRoom);
        if (group is null)
            throw new InvalidOperationException("No Sonos room available for wake.");

        if (!string.IsNullOrWhiteSpace(room) &&
            !string.Equals(group.CoordinatorRoom, room, StringComparison.OrdinalIgnoreCase) &&
            !_sonos.Groups.Any(g => string.Equals(g.CoordinatorRoom, room, StringComparison.OrdinalIgnoreCase)))
        {
            AppLog.Warn($"Wake room '{room}' not found; using {group.CoordinatorRoom}");
        }

        var controller = _sonos.CreateControllerForRoom(group.CoordinatorRoom)
            ?? throw new InvalidOperationException($"Could not control wake room '{group.DisplayName}'.");
        var memberIps = _sonos.MemberIpsForCoordinator(group.CoordinatorUuid);
        if (memberIps.Count == 0)
            throw new InvalidOperationException($"No speakers in wake group '{group.DisplayName}'.");

        var start = Math.Clamp(settings.WakeStartVolume, 0, 100);
        var end = Math.Clamp(settings.WakeEndVolume, 0, 100);
        _status($"Wake: starting on {group.DisplayName}…");
        AppLog.Info($"Wake Phase A: room={group.CoordinatorRoom}, start={start}%, end={end}%, source={settings.WakeSource}");

        await _sonos.SetVolumesAbsoluteAsync(memberIps, start, ct).ConfigureAwait(false);

        if (string.Equals(settings.WakeSource, AppSettings.WakeSourceFavorite, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(settings.WakeFavoriteName))
                throw new InvalidOperationException("Wake source is Favorite but no favorite is selected.");
            await controller.PlayFavoriteByNameAsync(settings.WakeFavoriteName, ct).ConfigureAwait(false);
            _status($"Wake: ▶ {settings.WakeFavoriteName} on {group.DisplayName}");
        }
        else
        {
            // Use history-aware shuffle via SonosManager (not bare controller).
            _sonos.SetActiveRoom(group.CoordinatorRoom);
            var summary = await _sonos.ShuffleWithHistoryAsync(ct).ConfigureAwait(false);
            _status($"Wake: 🔀 shuffling on {group.DisplayName} ({summary})");
        }

        _rampCoordinatorUuid = group.CoordinatorUuid;
        _rampCoordinatorIp = group.CoordinatorIp;
        _rampCoordinatorRoom = group.CoordinatorRoom;
        _rampRoomLabel = group.DisplayName;
        _rampMemberIps = memberIps;
        _rampVolume = start;
        _rampEnd = end;
        _rampStep = Math.Max(1, settings.WakeVolumeStep);
        _rampInterval = TimeSpan.FromMinutes(Math.Max(1, settings.WakeStepIntervalMinutes));
    }

    private string? _rampCoordinatorUuid;
    private string? _rampCoordinatorIp;
    private string? _rampCoordinatorRoom;
    private string _rampRoomLabel = "";
    private IReadOnlyList<string> _rampMemberIps = [];
    private int _rampVolume;
    private int _rampEnd;
    private int _rampStep;
    private TimeSpan _rampInterval;

    /// <returns>True if ramp reached end without cancellation.</returns>
    private async Task<bool> RampAsync(AppSettings settings, CancellationToken ct)
    {
        if (_rampMemberIps.Count == 0 || _rampCoordinatorUuid is null)
            return false;

        if (_rampVolume >= _rampEnd)
        {
            AppLog.Info("Wake ramp: already at end volume");
            return true;
        }

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_rampInterval, ct).ConfigureAwait(false);

            var next = Math.Min(_rampVolume + _rampStep, _rampEnd);
            // Re-resolve members in case topology changed mid-ramp.
            var ips = _sonos.MemberIpsForCoordinator(_rampCoordinatorUuid);
            if (ips.Count == 0)
                ips = _rampMemberIps;

            await _sonos.SetVolumesAbsoluteAsync(ips, next, ct).ConfigureAwait(false);
            _rampVolume = next;
            AppLog.Info($"Wake volume → {next}% ({_rampRoomLabel})");
            _status($"Wake volume → {next}% ({_rampRoomLabel})");

            if (_rampVolume >= _rampEnd)
                return true;
        }

        return false;
    }

    private async Task ExecutePhaseBAsync(AppSettings settings, CancellationToken ct)
    {
        if (_rampCoordinatorUuid is null || _rampCoordinatorIp is null)
            return;

        _status("Wake: expanding to all speakers…");
        AppLog.Info($"Wake Phase B: expand to house + library shuffle (coord={_rampCoordinatorUuid})");

        await _sonos.GroupAllSpeakersToAsync(_rampCoordinatorUuid, ct).ConfigureAwait(false);
        await _sonos.SetVolumesAbsoluteAsync(_sonos.AllVisibleIps(), _rampEnd, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(_rampCoordinatorRoom))
            _sonos.SetActiveRoom(_rampCoordinatorRoom);
        var summary = await _sonos.ShuffleWithHistoryAsync(ct).ConfigureAwait(false);

        AppLog.Info($"Wake Phase B complete: whole-house library shuffle ({summary})");
        _status($"Wake: 🔀 all speakers, library shuffle ({summary})");
    }

    private void CancelPreviousWakeCts()
    {
        var old = _wakeCts;
        _wakeCts = null;
        if (old is null)
            return;
        try { old.Cancel(); } catch { /* ignore */ }
        try { old.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>Next local DateTime at minutes-since-midnight on a selected day, or null.</summary>
    public static DateTime? ComputeNextFire(DateTime now, int wakeMinutes, int daysMask)
    {
        if (daysMask == 0)
            return null;

        wakeMinutes = Math.Clamp(wakeMinutes, 0, 1439);
        var todayStart = now.Date.AddMinutes(wakeMinutes);

        for (var offset = 0; offset < 8; offset++)
        {
            var day = now.Date.AddDays(offset);
            var candidate = day.AddMinutes(wakeMinutes);
            if (offset == 0 && candidate <= now)
                continue;
            if ((daysMask & (1 << (int)day.DayOfWeek)) == 0)
                continue;
            return candidate;
        }

        // Fallback: today+7 at wake time if loop somehow misses (shouldn't).
        return todayStart.AddDays(7);
    }

    public void Dispose()
    {
        CancelPreviousWakeCts();
        _scheduleTimer?.Dispose();
        _scheduleTimer = null;
        Interlocked.Exchange(ref _active, 0);
    }
}
