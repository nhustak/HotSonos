using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HotSonos.App.Infrastructure;
using HotSonos.App.Mcp;
using HotSonos.App.Models;
using HotSonos.App.Services;
using HotSonos.App.Windows;
using HotSonos.Core.Models;

namespace HotSonos.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "HotSonos.SingleInstance.A0E1";

    private Mutex? _singleInstanceMutex;
    private ConfigStore _store = null!;
    private AppSettings _settings = null!;
    private SonosManager _sonos = null!;
    private GlobalHotkeyManager _hotkeys = null!;
    private TrayController _tray = null!;
    private NowPlayingFlyout? _flyout;
    private NowPlaying? _lastNowPlaying;
    private MainWindow? _mainWindow;
    private System.Threading.Timer? _nightlyTimer;
    private WakeMusicService? _wake;
    private HotSonosMcpHost? _mcpHost;
    private HotSonosMcpState? _mcpState;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            // Another HotSonos is already running; exit quietly.
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Software rendering avoids GPU/driver glitches that can freeze or blank
        // always-on tray utilities on some multi-monitor / hybrid-GPU setups
        // (same approach as HotNotify). Slightly higher CPU than hardware render.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        AppLog.Info($"Starting {AppVersion.DisplayName} (args: {string.Join(' ', e.Args)})");

        // A tray utility must survive stray errors (e.g. flaky album-art loads or
        // event-callback hiccups) rather than vanish. Log + surface instead.
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            AppLog.Error("Dispatcher unhandled exception", ex.Exception);
            try { _tray?.ShowBalloon("HotSonos", $"Recovered from an error: {ex.Exception.Message}"); }
            catch (Exception balloonEx) { AppLog.Warn("Balloon after dispatcher error failed", balloonEx); }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            AppLog.Error("Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        _store = new ConfigStore();
        _settings = _store.Load();

        _sonos = new SonosManager();
        _sonos.NowPlayingChanged += OnNowPlayingChanged;
        _sonos.TopologyChanged += OnTopologyChanged;
        _sonos.SpeakerAvailabilityChanged += OnSpeakerAvailabilityChanged;

        _hotkeys = new GlobalHotkeyManager();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        _wake = new WakeMusicService(
            _sonos,
            () => _settings,
            status => Dispatcher.InvokeAsync(() =>
            {
                if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                    EnsureFlyout().ShowAction(status);
            }),
            () => Dispatcher.InvokeAsync(() => _tray?.SetWakeActive(_wake?.IsActive == true)),
            _actionGate);

        _mcpState = new HotSonosMcpState
        {
            Sonos = _sonos,
            Settings = () => _settings,
            Wake = _wake,
            GetLastNowPlaying = () => _lastNowPlaying,
            RefreshDevicesAsync = McpRefreshDevicesAsync,
        };
        _mcpHost = new HotSonosMcpHost();

        _tray = new TrayController(
            AppVersion.DisplayName,
            new TrayController.Callbacks(
                OpenSettings: ShowMainWindow,
                Refresh: OnTrayRefresh,
                FreshStart: () => _ = ExecuteActionAsync(HotsonosAction.FreshStart),
                ShuffleLibrary: () => _ = ExecuteActionAsync(HotsonosAction.ShuffleLibrary),
                PlayPause: () => _ = ExecuteActionAsync(HotsonosAction.PlayPause),
                Next: () => _ = ExecuteActionAsync(HotsonosAction.Next),
                Previous: () => _ = ExecuteActionAsync(HotsonosAction.Previous),
                VolumeUp: () => _ = ExecuteActionAsync(HotsonosAction.VolumeUp),
                VolumeDown: () => _ = ExecuteActionAsync(HotsonosAction.VolumeDown),
                Mute: () => _ = ExecuteActionAsync(HotsonosAction.Mute),
                PlayFavoriteSlot: slot => _ = ExecuteActionAsync(HotsonosAction.Favorite1 + slot),
                LevelVolumes: () => _ = ExecuteActionAsync(HotsonosAction.LevelVolumes),
                SetRoom: OnTraySetRoom,
                OpenLogFolder: () => AppLog.OpenLogFolder(),
                CopyDiagnostics: OnCopyDiagnostics,
                StopWake: () => _wake?.Cancel(),
                CopyMcpEndpoint: OnCopyMcpEndpoint,
                Exit: ExitApplication));

        var failures = ApplyBindings();
        if (failures.Count > 0)
            AppLog.Warn($"Hotkey registration failed for: {string.Join(", ", failures)}");

        var launchedFromAutorun = e.Args.Any(a => string.Equals(a, WindowsStartupManager.AutorunArgument, StringComparison.OrdinalIgnoreCase));
        if (!launchedFromAutorun)
            ShowMainWindow(); // manual launches open Settings directly; Windows autorun stays silent in the tray

        _ = InitialDiscoveryAsync();
        _ = StartMcpIfEnabledAsync();
    }

    private async Task StartMcpIfEnabledAsync()
    {
        if (_mcpHost is null || _mcpState is null || !_settings.McpEnabled)
        {
            AppLog.Info("MCP disabled in settings");
            return;
        }

        try
        {
            await _mcpHost.StartAsync(_mcpState, _settings.McpPort).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
                _tray?.SetMcpEndpoint(_mcpHost.Endpoint));
        }
        catch (Exception ex)
        {
            AppLog.Error("MCP server failed to start (is the port in use?)", ex);
            try
            {
                _tray?.ShowBalloon("HotSonos", $"MCP failed to start on port {_settings.McpPort}: {ex.Message}");
            }
            catch { /* ignore */ }
        }
    }

    private async Task<string> McpRefreshDevicesAsync()
    {
        await _sonos.RefreshAsync(_settings.ActiveRoom).ConfigureAwait(false);
        _settings.ActiveRoom ??= _sonos.ActiveRoom;
        await Dispatcher.InvokeAsync(() =>
        {
            UpdateTrayDynamic();
            // Settings window re-populates from the same manager when open.
            if (_mainWindow is { IsVisible: true })
                _mainWindow.RefreshDevicesInBackground();
        });
        return $"OK: {_sonos.Groups.Count} group(s), active={_sonos.ActiveRoom ?? "(none)"}, offline=[{string.Join(", ", _sonos.OfflineSpeakers)}]";
    }

    private void OnCopyMcpEndpoint()
    {
        var ep = _mcpHost?.Endpoint ?? $"http://127.0.0.1:{_settings.McpPort}/mcp (not running)";
        try
        {
            System.Windows.Clipboard.SetText(ep);
            _tray.ShowBalloon("HotSonos", _mcpHost?.IsRunning == true
                ? $"MCP endpoint copied:\n{ep}"
                : "MCP is not running — endpoint pattern copied.");
            AppLog.Info($"MCP endpoint copied: {ep}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Copy MCP endpoint failed", ex);
        }
    }

    private void OnCopyDiagnostics()
    {
        if (AppLog.TryCopyRecentToClipboard())
        {
            AppLog.Info("Diagnostics copied to clipboard");
            _tray.ShowBalloon("HotSonos", "Recent log copied to clipboard.");
        }
        else
        {
            _tray.ShowBalloon("HotSonos", "Could not copy diagnostics — open the log folder instead.");
        }
    }

    private async Task InitialDiscoveryAsync()
    {
        try
        {
            await _sonos.RefreshAsync(_settings.ActiveRoom);
            _settings.ActiveRoom ??= _sonos.ActiveRoom;
            UpdateTrayDynamic();
            AppLog.Info($"Initial discovery: {_sonos.Groups.Count} group(s), active={_settings.ActiveRoom ?? "(none)"}");
        }
        catch (Exception ex)
        {
            // Discovery failures are non-fatal; the user can Refresh from the tray.
            AppLog.Warn("Initial discovery failed", ex);
        }
    }

    /// <summary>Re-registers hotkeys from settings and refreshes the tray; returns failures.</summary>
    private IReadOnlyList<HotsonosAction> ApplyBindings()
    {
        var failures = _hotkeys.ApplyBindings(_settings);
        UpdateTrayDynamic();
        ScheduleNightlyReset();
        _wake?.Schedule();
        return failures;
    }

    /// <summary>Arms a one-shot timer for the next nightly reset; re-arms itself after firing.</summary>
    private void ScheduleNightlyReset()
    {
        _nightlyTimer?.Dispose();
        _nightlyTimer = null;
        if (!_settings.NightlyResetEnabled)
            return;

        var now = DateTime.Now;
        var target = now.Date.AddMinutes(_settings.NightlyResetMinutes);
        if (target <= now)
            target = target.AddDays(1);

        AppLog.Info($"Nightly reset scheduled for {target:yyyy-MM-dd HH:mm} (reshuffle={_settings.NightlyResetReshuffle})");
        _nightlyTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                var status = await _sonos.NightlyResetAsync(_settings.NightlyResetReshuffle);
                AppLog.Info($"Nightly reset: {status}");
            }
            catch (Exception ex)
            {
                AppLog.Error("Nightly reset failed", ex);
            }
            await Dispatcher.InvokeAsync(ScheduleNightlyReset); // re-arm for tomorrow
        }, null, target - now, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void UpdateTrayDynamic()
    {
        var groups = _sonos.Groups.Select(g => (g.DisplayName, g.CoordinatorRoom)).ToList();
        _tray.UpdateRooms(groups, _settings.ActiveRoom ?? _sonos.ActiveRoom);
        _tray.UpdateFavorites(_settings.FavoriteSlots.Select(s => s.FavoriteName).ToList());
        _tray.UpdateOfflineSpeakers(_sonos.OfflineSpeakers);
    }

    private async void OnHotkeyPressed(HotsonosAction action) => await ExecuteActionAsync(action);

    /// <summary>
    /// True for multi-second library work that must not stack (queue clear/enqueue races).
    /// </summary>
    private static bool IsExclusiveAction(HotsonosAction action) =>
        action is HotsonosAction.ShuffleLibrary or HotsonosAction.FreshStart;

    private static bool IsVolumeAction(HotsonosAction action) =>
        action is HotsonosAction.VolumeUp or HotsonosAction.VolumeDown
            or HotsonosAction.Mute or HotsonosAction.LevelVolumes;

    private async Task ExecuteActionAsync(HotsonosAction action)
    {
        // User volume control cancels an in-progress wake ramp (they take over).
        if (IsVolumeAction(action) && _wake?.IsActive == true)
            _wake.Cancel();

        // Exclusive actions refuse re-entry immediately so a double hotkey cannot
        // interleave two shuffles. Other actions wait their turn so volume/skip
        // still run after a long shuffle finishes.
        if (IsExclusiveAction(action))
        {
            if (!await _actionGate.WaitAsync(0))
            {
                AppLog.Info($"Ignored concurrent exclusive action: {action}");
                if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                    EnsureFlyout().ShowAction("Busy — already re-syncing / shuffling…");
                return;
            }
        }
        else
        {
            await _actionGate.WaitAsync();
        }

        try
        {
            // FreshStart re-discovers (SSDP) then regroups then shuffles, which can take
            // several seconds; acknowledge the keypress immediately so it doesn't look ignored.
            if (action == HotsonosAction.FreshStart && (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned))
                EnsureFlyout().ShowAction("🔄 Fresh start: re-syncing…");

            try
            {
                AppLog.Info($"Action {action}");
                var toast = await _sonos.ExecuteAsync(action, _settings);
                if (!string.IsNullOrEmpty(toast))
                {
                    AppLog.Info($"Action {action} → {toast}");
                    if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                        EnsureFlyout().ShowAction(toast!);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"Action {action} failed", ex);
                EnsureFlyout().ShowAction($"Sonos error: {ex.Message}"); // errors always surface
            }
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private NowPlayingFlyout EnsureFlyout()
    {
        if (_flyout is null)
        {
            _flyout = new NowPlayingFlyout(_settings, TrySaveSettings);
            if (_lastNowPlaying is not null)
                _flyout.ShowNowPlaying(_lastNowPlaying);
        }
        return _flyout;
    }

    private void OnTopologyChanged() =>
        Dispatcher.InvokeAsync(UpdateTrayDynamic);

    private void OnSpeakerAvailabilityChanged(string room, bool isOnline) =>
        Dispatcher.InvokeAsync(async () =>
        {
            if (isOnline)
            {
                try { await _sonos.GroupAllSpeakersAsync(); }
                catch (Exception ex)
                {
                    // Best-effort rejoin; still confirm it's back via flyout.
                    AppLog.Warn($"Rejoin after reconnect failed for {room}", ex);
                }
            }
            var message = isOnline ? $"✓ {room} rejoined the group" : $"⚠️ {room} dropped off the network";
            AppLog.Info(isOnline ? $"Speaker online: {room}" : $"Speaker offline: {room}");
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                EnsureFlyout().ShowAction(message);
        });

    private void OnNowPlayingChanged(NowPlaying nowPlaying)
    {
        // Raised on a background (listener) thread — marshal to the UI thread.
        Dispatcher.InvokeAsync(() =>
        {
            _lastNowPlaying = nowPlaying;
            _tray.UpdateNowPlaying(nowPlaying.IsEmpty ? null : nowPlaying.DisplayLine);
            if (_settings.ShowFlyoutOnTrackChange || _settings.FlyoutPinned)
                EnsureFlyout().ShowNowPlaying(nowPlaying);
        });
    }

    private void OnTrayRefresh() => _ = OnTrayRefreshAsync();

    private async Task OnTrayRefreshAsync()
    {
        try
        {
            await _sonos.RefreshAsync(_settings.ActiveRoom);
            _settings.ActiveRoom ??= _sonos.ActiveRoom;
            UpdateTrayDynamic();
            _tray.ShowBalloon("HotSonos", $"Found {_sonos.Groups.Count} speaker group(s).");
        }
        catch (Exception ex)
        {
            AppLog.Error("Tray refresh discovery failed", ex);
            _tray.ShowBalloon("HotSonos", $"Discovery failed: {ex.Message}");
        }
    }

    private void OnTraySetRoom(string room)
    {
        _sonos.SetActiveRoom(room);
        _settings.ActiveRoom = room;
        TrySaveSettings();
        UpdateTrayDynamic();
    }

    private void OnRoomChangedFromWindow(string room)
    {
        _settings.ActiveRoom = room;
        UpdateTrayDynamic();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(_sonos, _store, _settings, ApplyBindings, OnRoomChangedFromWindow,
                action => _ = ExecuteActionAsync(action));
            _mainWindow.HideToTrayRequested += (_, _) => _mainWindow?.Hide();
            _mainWindow.Closing += OnMainWindowClosing;
        }

        if (!_mainWindow.IsVisible)
            _mainWindow.Show(); // IsVisibleChanged kicks off device discovery
        else
            _mainWindow.RefreshDevicesInBackground(); // already open: still re-discover

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
            return;

        e.Cancel = true;
        _mainWindow?.Hide();
    }

    private void TrySaveSettings()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            // Non-fatal: a failed save just means the room choice isn't persisted.
            AppLog.Error("Settings save failed", ex);
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        AppLog.Info("Exit requested");

        _nightlyTimer?.Dispose();
        _wake?.Dispose();
        try { _mcpHost?.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { /* exit */ }
        _hotkeys?.Dispose();

        // Best-effort unsubscribe so speakers do not hold dead SIDs until timeout.
        // Block briefly at process exit; do not hang forever if a speaker is offline.
        if (_sonos is not null)
        {
            try
            {
                var dispose = _sonos.DisposeEventsAsync().AsTask();
                if (!dispose.Wait(TimeSpan.FromSeconds(2)))
                    AppLog.Warn("Event dispose timed out after 2s on exit");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Event dispose on exit failed", ex);
            }
        }

        _flyout?.HardClose();
        _actionGate.Dispose();

        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.Close();
        }

        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
