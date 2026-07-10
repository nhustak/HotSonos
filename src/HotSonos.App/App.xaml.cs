using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HotSonos.App.Infrastructure;
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
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // A tray utility must survive stray errors (e.g. flaky album-art loads or
        // event-callback hiccups) rather than vanish. Swallow + surface instead.
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            try { _tray?.ShowBalloon("HotSonos", $"Recovered from an error: {ex.Exception.Message}"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => ex.SetObserved();

        _store = new ConfigStore();
        _settings = _store.Load();

        _sonos = new SonosManager();
        _sonos.NowPlayingChanged += OnNowPlayingChanged;
        _sonos.TopologyChanged += OnTopologyChanged;
        _sonos.SpeakerAvailabilityChanged += OnSpeakerAvailabilityChanged;

        _hotkeys = new GlobalHotkeyManager();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;

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
                Exit: ExitApplication));

        ApplyBindings();

        var launchedFromAutorun = e.Args.Any(a => string.Equals(a, WindowsStartupManager.AutorunArgument, StringComparison.OrdinalIgnoreCase));
        if (!launchedFromAutorun)
            ShowMainWindow(); // manual launches open Settings directly; Windows autorun stays silent in the tray

        _ = InitialDiscoveryAsync();
    }

    private async Task InitialDiscoveryAsync()
    {
        try
        {
            await _sonos.RefreshAsync(_settings.ActiveRoom);
            _settings.ActiveRoom ??= _sonos.ActiveRoom;
            UpdateTrayDynamic();
        }
        catch
        {
            // Discovery failures are non-fatal; the user can Refresh from the tray.
        }
    }

    /// <summary>Re-registers hotkeys from settings and refreshes the tray; returns failures.</summary>
    private IReadOnlyList<HotsonosAction> ApplyBindings()
    {
        var failures = _hotkeys.ApplyBindings(_settings);
        UpdateTrayDynamic();
        ScheduleNightlyReset();
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

        _nightlyTimer = new System.Threading.Timer(async _ =>
        {
            try { await _sonos.NightlyResetAsync(); } catch { }
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

    private async Task ExecuteActionAsync(HotsonosAction action)
    {
        try
        {
            var toast = await _sonos.ExecuteAsync(action, _settings);
            if (!string.IsNullOrEmpty(toast) && (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned))
                EnsureFlyout().ShowAction(toast!);
        }
        catch (Exception ex)
        {
            EnsureFlyout().ShowAction($"Sonos error: {ex.Message}"); // errors always surface
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
                catch { /* best-effort rejoin; still confirm it's back via balloon */ }
            }
            var message = isOnline ? $"✓ {room} rejoined the group" : $"⚠️ {room} dropped off the network";
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
            _mainWindow.Show();
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
        catch
        {
            // Non-fatal: a failed save just means the room choice isn't persisted.
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;

        _nightlyTimer?.Dispose();
        _hotkeys?.Dispose();
        _ = _sonos?.DisposeEventsAsync();
        _flyout?.HardClose();

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
