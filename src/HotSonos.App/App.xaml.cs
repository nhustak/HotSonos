using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HotSonos.App.Infrastructure;
using HotSonos.App.Models;
using HotSonos.App.Services;
using HotSonos.App.Windows;

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
    private ToastWindow? _toast;
    private MainWindow? _mainWindow;
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

        _store = new ConfigStore();
        _settings = _store.Load();

        _sonos = new SonosManager();

        _hotkeys = new GlobalHotkeyManager();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        _tray = new TrayController(
            AppVersion.DisplayName,
            new TrayController.Callbacks(
                OpenSettings: ShowMainWindow,
                Refresh: OnTrayRefresh,
                ShuffleLibrary: () => _ = ExecuteActionAsync(HotsonosAction.ShuffleLibrary),
                PlayPause: () => _ = ExecuteActionAsync(HotsonosAction.PlayPause),
                Next: () => _ = ExecuteActionAsync(HotsonosAction.Next),
                Previous: () => _ = ExecuteActionAsync(HotsonosAction.Previous),
                PlayFavoriteSlot: slot => _ = ExecuteActionAsync(HotsonosAction.Favorite1 + slot),
                SetRoom: OnTraySetRoom,
                Exit: ExitApplication));

        ApplyBindings();

        var launchedFromAutorun = e.Args.Any(a => string.Equals(a, WindowsStartupManager.AutorunArgument, StringComparison.OrdinalIgnoreCase));
        if (!launchedFromAutorun)
            _tray.ShowBalloon("HotSonos", "Running in the tray. Double-click to shuffle your library to all speakers; right-click for settings.");

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
        return failures;
    }

    private void UpdateTrayDynamic()
    {
        var groups = _sonos.Groups.Select(g => (g.DisplayName, g.CoordinatorRoom)).ToList();
        _tray.UpdateRooms(groups, _settings.ActiveRoom ?? _sonos.ActiveRoom);
        _tray.UpdateFavorites(_settings.FavoriteSlots.Select(s => s.FavoriteName).ToList());
    }

    private async void OnHotkeyPressed(HotsonosAction action) => await ExecuteActionAsync(action);

    private async Task ExecuteActionAsync(HotsonosAction action)
    {
        try
        {
            var toast = await _sonos.ExecuteAsync(action, _settings);
            if (_settings.ShowToast && !string.IsNullOrEmpty(toast))
                ShowToast(toast!);
        }
        catch (Exception ex)
        {
            ShowToast($"Sonos error: {ex.Message}");
        }
    }

    private void ShowToast(string message)
    {
        _toast ??= new ToastWindow();
        _toast.ShowMessage(message);
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
            _mainWindow = new MainWindow(_sonos, _store, _settings, ApplyBindings, OnRoomChangedFromWindow);
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

        _hotkeys?.Dispose();
        _toast?.HardClose();

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
