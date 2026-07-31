using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HotSonos.App.Infrastructure;
using HotSonos.App.Library;
using HotSonos.App.Mcp;
using HotSonos.App.Models;
using HotSonos.App.Services;
using HotSonos.App.Windows;
using HotSonos.Core.Models;

namespace HotSonos.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "HotSonos.SingleInstance.A0E1";
    private const string ShowWindowEventName = "Local\\HotSonos.ShowWindow.A0E1";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private Thread? _showWindowListener;
    private ConfigStore _store = null!;
    private AppSettings _settings = null!;
    private SonosManager _sonos = null!;
    private GlobalHotkeyManager _hotkeys = null!;
    private TrayController _tray = null!;
    private NowPlayingFlyout? _flyout;
    private NowPlaying? _lastNowPlaying;
    private MainWindow? _mainWindow;
    private System.Threading.Timer? _nightlyTimer;
    private System.Threading.Timer? _heartbeatTimer; // unused; UI timer below
    private System.Windows.Threading.DispatcherTimer? _heartbeatUiTimer;
    private WakeMusicService? _wake;
    private HotSonosMcpHost? _mcpHost;
    private HotSonosMcpState? _mcpState;
    private LibraryService? _library;
    private System.Threading.Timer? _pendingTagTimer;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    /// <summary>Coalesced ±volume while gate/Sonos is slow — never one hotkey = one queued full action.</summary>
    private int _pendingVolumeDelta;
    private int _volumeDrainRunning; // 0/1
    private bool _isExiting;
    /// <summary>False until first discovery finishes — GENA/flyout must not run UI work before tray + room exist.</summary>
    private bool _startupReady;
    private Window? _keepAliveWindow;


    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            // Another instance owns the tray — ask it to show the window, then exit.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var showEvt))
                {
                    showEvt.Set();
                    showEvt.Dispose();
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        "HotSonos is already running (check the system tray near the clock).\n\n" +
                        "If you don't see the icon, open the hidden icons overflow (^).",
                        "HotSonos",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch
            {
                System.Windows.MessageBox.Show(
                    "HotSonos is already running in the system tray.",
                    "HotSonos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            AppLog.Lifecycle("Second instance exit (single-instance mutex held)");
            Shutdown();
            return;
        }

        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _showWindowListener = new Thread(ShowWindowListenerLoop)
            {
                IsBackground = true,
                Name = "HotSonos.ShowWindowListener",
            };
            _showWindowListener.Start();
        }
        catch (Exception ex)
        {
            // Non-fatal: second-instance activate just won't work.
            System.Diagnostics.Debug.WriteLine(ex);
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Software rendering avoids GPU/driver glitches that can freeze or blank
        // always-on tray utilities on some multi-monitor / hybrid-GPU setups
        // (same approach as HotNotify). Slightly higher CPU than hardware render.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // Hidden non-transparent WPF window — required so Application.Run keeps a live
        // dispatcher when UI is tray-only (WinForms NotifyIcon). Without this the process
        // can exit ~15–20s after startup with no exception once async startup completes.
        _keepAliveWindow = new Window
        {
            Title = "HotSonos",
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Left = -32000,
            Top = -32000,
        };
        _keepAliveWindow.Show();

        AppLog.Lifecycle($"Starting {AppVersion.DisplayName} pid={Environment.ProcessId} (args: {string.Join(' ', e.Args)})");

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
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            var err = ex.ExceptionObject as Exception;
            AppLog.Error("AppDomain unhandled exception (IsTerminating=" + ex.IsTerminating + ")", err);
            AppLog.Lifecycle(
                $"AppDomain unhandled IsTerminating={ex.IsTerminating}: " +
                (err?.GetType().Name ?? "?") + " " + (err?.Message ?? ex.ExceptionObject?.ToString() ?? "?"));
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // Last chance — may run after WPF teardown; keep it tiny and durable.
            AppLog.Lifecycle(
                $"ProcessExit uptime={_uptime.Elapsed:hh\\:mm\\:ss} isExiting={_isExiting} " +
                $"exitCode={Environment.ExitCode}");
        };
        Exit += (_, args) =>
        {
            AppLog.Lifecycle($"WPF Exit Application.ExitCode={args.ApplicationExitCode} isExiting={_isExiting}");
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            try { _heartbeatUiTimer?.Stop(); } catch { /* ignore */ }
            _heartbeatUiTimer = null;
        };

        try
        {
        _store = new ConfigStore();
        _settings = _store.Load().EnsureShape();
        // Crash dumps: System.StackOverflowException in PresentationFramework under topology thrash.
        // Monitor is debug-only; never leave it ON across restarts (GENA + UI map + GroupAll cascade).
        if (_settings.TopologyMonitorEnabled)
        {
            _settings.TopologyMonitorEnabled = false;
            AppLog.Warn("Topology monitor was ON — forced OFF at startup for stability (re-enable on Topology tab if debugging)");
        }
        // Hard isolation while process is dying ~4min after start with no managed exception:
        // ASP.NET MCP host in-process has been on every crashing run; keep tray stable first.
        if (_settings.McpEnabled)
        {
            _settings.McpEnabled = false;
            AppLog.Warn("MCP forced OFF at startup (stability isolation) — re-enable on MCP Debug tab when stable");
        }
        if (_settings.AutoRecoverPlayback)
        {
            _settings.AutoRecoverPlayback = false;
            AppLog.Warn("AutoRecoverPlayback forced OFF at startup (stability isolation)");
        }
        if (_settings.ShowFlyoutOnTrackChange)
        {
            _settings.ShowFlyoutOnTrackChange = false;
            AppLog.Warn("ShowFlyoutOnTrackChange forced OFF at startup (stability isolation)");
        }
        // Persist freshly seeded tag catalog (or other EnsureShape defaults) once.
        try { _store.Save(_settings); }
        catch (Exception ex) { AppLog.Warn("Settings save after load/normalize failed", ex); }

        // Isolation: no GENA, no poll — only discovery + tray + hotkeys (SOAP on demand).
        SonosManager.UseGenaSubscriptions = false;
        SonosManager.UseNowPlayingPoll = true;
        _sonos = new SonosManager(() => _settings);
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

        _library = new LibraryService(
            () => _settings,
            discoverRootsFromSonos: ct => _sonos.DiscoverMusicLibraryRootsAsync(ct),
            persistSettings: () =>
            {
                try { _store.Save(_settings); }
                catch (Exception ex) { AppLog.Warn("Settings save after library root discovery failed", ex); }
            });
        // One-shot: map leftover slow/medium/… tokens → catalog keys; clear HOTSONOS_TEMPO.
        // Must NOT re-run every launch — was dual-writing 100+ FLACs to NAS and thrashing TagLib.
        ScheduleLegacyTagMigrationOnce();
        // Retry tag writes that were deferred while Sonos had the file open.
        // No pending-tag Threading.Timer (isolation). Heartbeat on UI DispatcherTimer only.
        _pendingTagTimer = null;
        _heartbeatUiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _heartbeatUiTimer.Tick += (_, _) =>
        {
            try
            {
                var wsMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
                var mode = _sonos is null
                    ? "?"
                    : _sonos.ShuffleSessionActive
                        ? "shuffle"
                        : _sonos.CanResumeShuffle
                            ? "special/folder"
                            : "none";
                var np = _lastNowPlaying?.DisplayLine ?? "(none)";
                if (np.Length > 60) np = np[..57] + "...";
                AppLog.Lifecycle(
                    $"Heartbeat uptime={_uptime.Elapsed:hh\\:mm\\:ss} ws={wsMb:F0}MB " +
                    $"groups={_sonos?.Groups.Count ?? 0} mode={mode} mcp={_mcpHost?.IsRunning == true} np={np}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Heartbeat failed", ex);
            }
        };
        _heartbeatUiTimer.Start();

        _mcpState = new HotSonosMcpState
        {
            Sonos = _sonos,
            Settings = () => _settings,
            Wake = _wake,
            Library = _library,
            GetLastNowPlaying = () => _lastNowPlaying,
            RefreshDevicesAsync = McpRefreshDevicesAsync,
            ExecuteActionAsync = McpExecuteActionAsync,
            SetActiveRoom = OnTraySetRoom,
            PersistSettings = () =>
            {
                try { _store.Save(_settings); }
                catch (Exception ex) { AppLog.Warn("Settings save from MCP failed", ex); }
            },
            PlayLibraryTrackAsync = McpPlayLibraryTrackAsync,
            ResumeShuffleAsync = McpResumeShuffleAsync,
            PlayTaggedTracksAsync = McpPlayTaggedTracksAsync,
            PlayGenreTracksAsync = McpPlayGenreTracksAsync,
            PlayLibraryFolderAsync = McpPlayLibraryFolderAsync,
        };
        _mcpHost = new HotSonosMcpHost();

        _tray = new TrayController(
            AppVersion.DisplayName,
            new TrayController.Callbacks(
                OpenSettings: ShowMainWindow,
                OpenMcpDebug: () => ShowMainWindowTab("mcp"),
                OpenLibrary: () => ShowMainWindowTab("library"),
                OpenTopology: () => ShowMainWindowTab("topology"),
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
                DoubleClick: OnTrayDoubleClick,
                Exit: ExitApplication));

        var failures = ApplyBindings();
        if (failures.Count > 0)
            AppLog.Warn($"Hotkey registration failed for: {string.Join(", ", failures)}");

        // Tray-only on start (open Settings from tray). Discover → ready → optional MCP.
        _ = StartupSequenceAsync(openMainWindow: false);

        // Isolation: no library auto-scan on start (disk thrash).
        }

        catch (Exception ex)
        {
            AppLog.Error("Fatal startup failure", ex);
            AppLog.Lifecycle($"Fatal startup failure: {ex.GetType().Name}: {ex.Message}");
            try
            {
                System.Windows.MessageBox.Show(
                    $"HotSonos failed to start:\n\n{ex.Message}\n\nSee logs under %LocalAppData%\\HotSonos\\logs",
                    "HotSonos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* ignore */ }
            Shutdown();
        }
    }

    /// <summary>
    /// Runs legacy tag/tempo migration at most once. After success, stamps
    /// <see cref="AppSettings.LegacyTagMigrationCompletedUtc"/> so restarts stay light.
    /// </summary>
    private void ScheduleLegacyTagMigrationOnce()
    {
        if (_library is null)
            return;

        if (!string.IsNullOrWhiteSpace(_settings.LegacyTagMigrationCompletedUtc))
        {
            AppLog.Info(
                $"Legacy tag migration already completed ({_settings.LegacyTagMigrationCompletedUtc}) — skipped");
            return;
        }

        // Prior builds rewrote all tagged files on every boot (dual-write to NAS). If we got
        // this far with a populated library, assume that work already happened — stamp and skip
        // so this launch is not another 100+ TagLib dual-write storm (crash suspect).
        if (_library.GetStatus().TrackCount > 0)
        {
            _settings.LegacyTagMigrationCompletedUtc = DateTime.UtcNow.ToString("o");
            try { _store.Save(_settings); }
            catch (Exception ex) { AppLog.Warn("Could not persist LegacyTagMigrationCompletedUtc", ex); }
            AppLog.Info(
                "Legacy tag migration assumed complete (library already scanned; prior boots rewrote tags) — skipped");
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                AppLog.Info("Legacy tag migration starting (one-shot, empty cache path)");
                var r = _library.MigrateLegacyTagTokens();
                AppLog.Info(r.Message);
                _settings.LegacyTagMigrationCompletedUtc = DateTime.UtcNow.ToString("o");
                try { _store.Save(_settings); }
                catch (Exception saveEx) { AppLog.Warn("Could not persist LegacyTagMigrationCompletedUtc", saveEx); }
                AppLog.Info("Legacy tag migration marked complete (will not re-run on next start)");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Legacy tag migration failed", ex);
            }
        });
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

    /// <summary>MCP control path: same gate/flyout behavior as hotkeys (marshaled to UI thread).</summary>
    private async Task<string> McpPlayLibraryTrackAsync(
        string path, string? title, string? artist, CancellationToken ct)
    {
        await _actionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AppLog.Info($"MCP play_library_track: {path}");
            var toast = await _sonos.PlayLibraryTrackAsync(path, title, artist, ct).ConfigureAwait(false);
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                await Dispatcher.InvokeAsync(() => EnsureFlyout().ShowAction(toast));
            return toast;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<string> McpResumeShuffleAsync(CancellationToken ct)
    {
        if (!await _actionGate.WaitAsync(0).ConfigureAwait(false))
            return "Busy — already re-syncing / shuffling…";

        try
        {
            AppLog.Info("MCP resume_shuffle");
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                await Dispatcher.InvokeAsync(() => EnsureFlyout().ShowAction("🔀 Resume shuffle…"));
            var toast = await _sonos.ResumeShuffleAsync(ct).ConfigureAwait(false);
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                await Dispatcher.InvokeAsync(() => EnsureFlyout().ShowAction(toast));
            return toast;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<string> McpPlayTaggedTracksAsync(string tag, bool shuffle, CancellationToken ct)
    {
        if (_library is null)
            throw new InvalidOperationException("Library service not available.");

        if (!await _actionGate.WaitAsync(0).ConfigureAwait(false))
            return "Busy — already re-syncing / shuffling…";

        try
        {
            AppLog.Info($"MCP play_tag: {tag} shuffle={shuffle}");
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                await Dispatcher.InvokeAsync(() => EnsureFlyout().ShowAction($"▶ {tag}…"));
            var toast = await _sonos.PlayTaggedTracksAsync(_library, tag, shuffle, ct).ConfigureAwait(false);
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                await Dispatcher.InvokeAsync(() => EnsureFlyout().ShowAction(toast));
            return toast;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<string> McpPlayGenreTracksAsync(string genre, bool shuffle, CancellationToken ct)
    {
        if (_library is null)
            throw new InvalidOperationException("Library service not available.");

        if (!await _actionGate.WaitAsync(0).ConfigureAwait(false))
            return "Busy — already re-syncing / shuffling…";

        try
        {
            AppLog.Info($"MCP play_genre: {genre} shuffle={shuffle}");
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                await Dispatcher.InvokeAsync(() => EnsureFlyout().ShowAction($"▶ Genre · {genre}…"));
            var toast = await _sonos.PlayGenreTracksAsync(_library, genre, shuffle, ct).ConfigureAwait(false);
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                await Dispatcher.InvokeAsync(() => EnsureFlyout().ShowAction(toast));
            return toast;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<string> McpPlayLibraryFolderAsync(string folderPath, bool shuffle, CancellationToken ct)
    {
        if (_library is null)
            throw new InvalidOperationException("Library service not available.");

        if (!await _actionGate.WaitAsync(0).ConfigureAwait(false))
            return "Busy — already re-syncing / shuffling…";

        try
        {
            AppLog.Info($"MCP play_folder: {folderPath} shuffle={shuffle}");
            var name = System.IO.Path.GetFileName(folderPath.TrimEnd('\\', '/'));
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                await Dispatcher.InvokeAsync(() => EnsureFlyout().ShowAction($"▶ Folder · {name}…"));
            await _sonos.GroupAllSpeakersAsync(ct).ConfigureAwait(false);
            var toast = await _sonos.PlayLibraryFolderAsync(_library, folderPath, shuffle, ct).ConfigureAwait(false);
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                await Dispatcher.InvokeAsync(() => EnsureFlyout().ShowAction(toast));
            return toast;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private Task<string?> McpExecuteActionAsync(HotsonosAction action)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                // Capture toast via Execute without duplicating gate logic: call the real path.
                await ExecuteActionAsync(action);
                tcs.TrySetResult($"OK:{action}");
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
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

    private async Task StartupSequenceAsync(bool openMainWindow)
    {
        try
        {
            await InitialDiscoveryAsync().ConfigureAwait(true);
            // MCP stays off in stability isolation (settings forced false).
            await StartMcpIfEnabledAsync().ConfigureAwait(true);
            if (openMainWindow)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    try { ShowMainWindow(); }
                    catch (Exception ex) { AppLog.Error("ShowMainWindow after discovery failed", ex); }
                });
            }

            AppLog.Info("Startup sequence complete (GENA off, MCP off, tray-only)");
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup sequence failed", ex);
            _startupReady = true;
        }
    }

    private async Task InitialDiscoveryAsync()
    {
        try
        {
            await _sonos.RefreshAsync(_settings.ActiveRoom).ConfigureAwait(true);
            _settings.ActiveRoom ??= _sonos.ActiveRoom;
            UpdateTrayDynamic();
            AppLog.Info($"Initial discovery: {_sonos.Groups.Count} group(s), active={_settings.ActiveRoom ?? "(none)"}");
        }
        catch (Exception ex)
        {
            // Discovery failures are non-fatal; the user can Refresh from the tray.
            AppLog.Warn("Initial discovery failed", ex);
        }
        finally
        {
            _startupReady = true;
            // Replay last GENA snapshot now that tray/controller exist (safe path).
            if (_lastNowPlaying is not null)
            {
                try { OnNowPlayingChanged(_lastNowPlaying); }
                catch (Exception ex) { AppLog.Warn("Deferred now-playing apply failed", ex); }
            }
            AppLog.Info("Startup ready (discovery finished)");
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
        _tray.UpdateFavorites(_settings.FavoriteSlots
            .Select(s => s.IsSet ? s.DisplayLabel(_settings) : null)
            .ToList());
        _tray.UpdateOfflineSpeakers(_sonos.OfflineSpeakers);
    }

    private async void OnHotkeyPressed(HotsonosAction action)
    {
        if (!_startupReady)
        {
            AppLog.Info($"Hotkey {action} ignored — still starting");
            return;
        }

        if (action == HotsonosAction.QuickTag)
        {
            ShowQuickTagOverlay();
            return;
        }

        if (action == HotsonosAction.QuickPlay)
        {
            _ = ShowQuickPlayOverlayAsync();
            return;
        }

        await ExecuteActionAsync(action);
    }

    /// <summary>HotLaunch-style overlay: digit keys apply tag presets to the playing library track.</summary>
    private void ShowQuickTagOverlay()
    {
        if (_library is null)
        {
            EnsureFlyout().ShowAction("Library service not available.");
            return;
        }

        var np = _lastNowPlaying;
        string? path = null;
        string? resolveMsg = null;
        string line = np is null || np.IsEmpty ? "(nothing playing)" : np.DisplayLine;

        if (np is null || np.IsEmpty)
        {
            resolveMsg = "Nothing playing.";
        }
        else if (string.IsNullOrWhiteSpace(np.TrackUri))
        {
            resolveMsg = "No track URI from Sonos.";
        }
        else
        {
            var track = _library.FindBySonosUri(np.TrackUri);
            if (track is null)
            {
                resolveMsg =
                    "Track not in library cache (streaming/radio, or run a rescan). " +
                    "Only Sonos Music Library files under configured roots can be tagged.";
            }
            else
            {
                path = track.Path;
            }
        }

        // Close any prior overlay instance
        foreach (Window w in Current.Windows)
        {
            if (w is QuickTagOverlay existing)
            {
                try { existing.Close(); } catch { /* ignore */ }
            }
        }

        var overlay = new QuickTagOverlay(_library, _settings, line, path, resolveMsg);
        overlay.Show();
        overlay.Activate();
    }

    /// <summary>
    /// HotLaunch-style picker: 1 = library shuffle, 2–9 = tags then Sonos favorites/playlists.
    /// </summary>
    private async Task ShowQuickPlayOverlayAsync()
    {
        IReadOnlyList<string> sonosTitles = [];
        try
        {
            var favorites = await _sonos.GetFavoritesAsync().ConfigureAwait(true);
            sonosTitles = favorites.Where(f => f.IsPlayable).Select(f => f.Title).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Quick play: favorites load failed", ex);
        }

        foreach (Window w in Current.Windows)
        {
            if (w is QuickPlayOverlay existing)
            {
                try { existing.Close(); } catch { /* ignore */ }
            }
        }

        var overlay = new QuickPlayOverlay(_sonos, _library, _settings, sonosTitles);
        overlay.Show();
        overlay.Activate();
    }

    /// <summary>
    /// True for multi-second library work that must not stack (queue clear/enqueue races).
    /// </summary>
    private static bool IsExclusiveAction(HotsonosAction action) =>
        action is HotsonosAction.ShuffleLibrary or HotsonosAction.FreshStart;

    private static bool IsVolumeAction(HotsonosAction action) =>
        action is HotsonosAction.VolumeUp or HotsonosAction.VolumeDown
            or HotsonosAction.Mute or HotsonosAction.LevelVolumes;

    private static bool IsVolumeStepAction(HotsonosAction action) =>
        action is HotsonosAction.VolumeUp or HotsonosAction.VolumeDown;

    private async Task ExecuteActionAsync(HotsonosAction action)
    {
        // User volume control cancels an in-progress wake ramp (they take over).
        if (IsVolumeAction(action) && _wake?.IsActive == true)
            _wake.Cancel();

        // Volume ± must NOT queue N separate actions behind a slow gate/topology storm.
        // That is what walked volume to 100%: each held keypress became +step after lag.
        // Coalesce into one pending delta (hard-capped) and apply once.
        if (IsVolumeStepAction(action))
        {
            QueueVolumeStep(action == HotsonosAction.VolumeUp ? 1 : -1);
            return;
        }

        // Exclusive actions refuse re-entry immediately so a double hotkey cannot
        // interleave two shuffles. Other actions wait their turn so skip/mute
        // still run after a long shuffle finishes.
        if (IsExclusiveAction(action))
        {
            if (!await _actionGate.WaitAsync(0))
            {
                AppLog.Info($"Ignored concurrent exclusive action: {action}");
                // Always surface — double-hit while scanning is easy to miss.
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
            // Acknowledge immediately so UI/hotkey doesn't look dead during long queue builds.
            if (action == HotsonosAction.FreshStart)
                EnsureFlyout().ShowAction("🔄 Fresh start: re-syncing…");
            else if (action == HotsonosAction.ShuffleLibrary)
                EnsureFlyout().ShowAction("🔀 Building shuffle queue…");

            try
            {
                AppLog.Info($"Action {action}");
                var toast = await _sonos.ExecuteAsync(action, _settings, library: _library);
                if (!string.IsNullOrEmpty(toast))
                {
                    AppLog.Info($"Action {action} → {toast}");
                    // Always show completion for exclusive work (user needs to know it finished).
                    if (IsExclusiveAction(action)
                        || _settings.ShowFlyoutOnAction
                        || _settings.FlyoutPinned)
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

    /// <summary>
    /// Accumulates volume hotkeys into a single bounded delta. Cap prevents
    /// "mash during lag → jump to 100%" even if many key events fire.
    /// </summary>
    private void QueueVolumeStep(int direction)
    {
        var step = Math.Max(1, _settings.EnsureShape().VolumeStep);
        var add = direction >= 0 ? step : -step;
        // At most ~4 steps of credit while delayed (e.g. step 5 → ±20%).
        var maxPend = Math.Clamp(step * 4, 10, 20);

        while (true)
        {
            var cur = Volatile.Read(ref _pendingVolumeDelta);
            var next = Math.Clamp(cur + add, -maxPend, maxPend);
            if (Interlocked.CompareExchange(ref _pendingVolumeDelta, next, cur) == cur)
                break;
        }

        _ = DrainPendingVolumeAsync();
    }

    private async Task DrainPendingVolumeAsync()
    {
        if (Interlocked.CompareExchange(ref _volumeDrainRunning, 1, 0) != 0)
            return;

        try
        {
            while (true)
            {
                var delta = Interlocked.Exchange(ref _pendingVolumeDelta, 0);
                if (delta == 0)
                    break;

                // Never wait on the shared action gate — shuffle/topology work must not delay volume.
                try
                {
                    var pct = await _sonos.AdjustVolumeByAsync(delta).ConfigureAwait(false);
                    var toast = pct > 0 ? $"🔊 Volume {pct}%" : "🔊 Volume adjusted";
                    // Toast only — no AppLog per press (disk I/O added lag under spam).
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                            EnsureFlyout().ShowAction(toast);
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Error("Volume delta failed", ex);
                    await Dispatcher.InvokeAsync(() =>
                        EnsureFlyout().ShowAction($"Sonos error: {ex.Message}"));
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _volumeDrainRunning, 0);
            // More keypresses arrived during the last apply — drain again.
            if (Volatile.Read(ref _pendingVolumeDelta) != 0)
                _ = DrainPendingVolumeAsync();
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

    private CancellationTokenSource? _keepHouseGroupedCts;
    private int _keepHouseGroupedInFlight; // 0/1
    private DateTime _keepHouseGroupedLastUtc = DateTime.MinValue;
    private DateTime _lastTrayTopologyRefreshUtc = DateTime.MinValue;

    private void OnTopologyChanged()
    {
        // Tray only — never do SOAP/regroup/UI map work on the GENA thread path.
        // Throttle: GENA can fire dozens of times during GroupAll; rebuilding the tray menu
        // that often contributed to WPF StackOverflowException (PresentationFramework).
        var now = DateTime.UtcNow;
        if ((now - _lastTrayTopologyRefreshUtc).TotalMilliseconds < 750)
            return;
        _lastTrayTopologyRefreshUtc = now;
        Dispatcher.BeginInvoke(UpdateTrayDynamic);
        MaybeScheduleKeepHouseGrouped();
    }

    /// <summary>
    /// Optional (settings off by default). Debounced + 60s cooldown. One GroupAll only —
    /// no refresh/retry storm (that delayed audio when topology flapped).
    /// </summary>
    private void MaybeScheduleKeepHouseGrouped()
    {
        if (!_settings.EnsureShape().KeepHouseGrouped)
            return;
        if (_sonos.Groups.Count <= 1)
            return;
        if ((DateTime.UtcNow - _keepHouseGroupedLastUtc).TotalSeconds < 60)
            return;

        try { _keepHouseGroupedCts?.Cancel(); } catch { /* ignore */ }
        _keepHouseGroupedCts?.Dispose();
        var cts = new CancellationTokenSource();
        _keepHouseGroupedCts = cts;
        _ = KeepHouseGroupedAfterDelayAsync(cts.Token);
    }

    private async Task KeepHouseGroupedAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(8000, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                return;
            if (!_settings.EnsureShape().KeepHouseGrouped)
                return;
            if (_sonos.Groups.Count <= 1)
                return;
            if ((DateTime.UtcNow - _keepHouseGroupedLastUtc).TotalSeconds < 60)
                return;
            if (System.Threading.Interlocked.CompareExchange(ref _keepHouseGroupedInFlight, 1, 0) != 0)
                return;

            try
            {
                var split = string.Join(" | ", _sonos.Groups.Select(g => g.DisplayName));
                AppLog.Info($"KeepHouseGrouped: {_sonos.Groups.Count} group(s) ({split}) — auto-regrouping (once)");
                _keepHouseGroupedLastUtc = DateTime.UtcNow;
                await _sonos.GroupAllSpeakersAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _keepHouseGroupedInFlight, 0);
            }
        }
        catch (OperationCanceledException)
        {
            /* debounce superseded */
        }
        catch (Exception ex)
        {
            System.Threading.Interlocked.Exchange(ref _keepHouseGroupedInFlight, 0);
            AppLog.Warn("KeepHouseGrouped auto-regroup failed", ex);
        }
    }

    private void OnSpeakerAvailabilityChanged(string room, bool isOnline) =>
        Dispatcher.BeginInvoke(() =>
        {
            // Notify only — never auto GroupAll from topology flaps. That cascade (GroupAll →
            // GENA flood → more online events) is the best match for hard process death after
            // recent "keep house grouped on reconnect" work. User can Regroup / Shuffle manually.
            var message = isOnline ? $"✓ {room} back online" : $"⚠️ {room} dropped off the network";
            AppLog.Info(isOnline ? $"Speaker online: {room} (no auto-regroup)" : $"Speaker offline: {room}");
            if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                EnsureFlyout().ShowAction(message);
        });

    private string? _lastUiNowPlayingKey;

    private void OnNowPlayingChanged(NowPlaying nowPlaying)
    {
        // Raised on a background (listener) thread — marshal to the UI thread.
        // Cache only until startup discovery finishes (GENA often fires before tray/controller ready).
        if (!_startupReady)
        {
            _lastNowPlaying = nowPlaying;
            return;
        }

        TryLogUnplayableNowPlaying(nowPlaying);

        var key = $"{nowPlaying.State}|{PlayHistoryStore.NormalizeKey(nowPlaying.TrackUri)}|{nowPlaying.Title}";
        var trackOrStateChanged = !string.Equals(key, _lastUiNowPlayingKey, StringComparison.Ordinal);
        if (!trackOrStateChanged)
            return;
        _lastUiNowPlayingKey = key;

        Dispatcher.BeginInvoke(() =>
        {
            if (_isExiting || _tray is null)
                return;

            _lastNowPlaying = nowPlaying;
            try { _tray.UpdateNowPlaying(nowPlaying.IsEmpty ? null : nowPlaying.DisplayLine); }
            catch (Exception ex) { AppLog.Warn("Tray now-playing update failed", ex); }

            // Flyout only on real track/state changes (already filtered). Art load is still expensive.
            if (_settings.ShowFlyoutOnTrackChange || _settings.FlyoutPinned)
            {
                try { EnsureFlyout().ShowNowPlaying(nowPlaying); }
                catch (Exception ex) { AppLog.Warn("Flyout now-playing update failed", ex); }
            }

            // Do NOT ProcessPendingTagWrites here — TagLib on the UI path under GENA was risky.
            // Timer every 12s handles the queue.
        });
    }

    private void ProcessPendingTagWritesSafe()
    {
        try
        {
            if (_library is null) return;
            var n = _library.ProcessPendingTagWrites();
            if (n > 0)
            {
                AppLog.Info($"Applied {n} queued tag write(s)");
                if (_settings.ShowFlyoutOnAction || _settings.FlyoutPinned)
                    EnsureFlyout().ShowAction($"Tagged {n} queued track(s)");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("ProcessPendingTagWrites failed", ex);
        }
    }

    /// <summary>
    /// When GENA reports a track (or TransportStatus error), look up the file in the
    /// library cache and log if format heuristics say Sonos should not play it.
    /// Live "skip because unplayable" is still imperfect — speakers often just advance.
    /// </summary>
    private void TryLogUnplayableNowPlaying(NowPlaying nowPlaying)
    {
        try
        {
            if (_library is null)
                return;

            var status = nowPlaying.TransportStatus;
            if (!string.IsNullOrWhiteSpace(status)
                && status.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn(
                    $"Sonos TransportStatus={status} title={nowPlaying.Title} uri={nowPlaying.TrackUri}");
                // Auto-recover is handled in SonosManager (Next → Play → reshuffle).
            }

            if (string.IsNullOrWhiteSpace(nowPlaying.TrackUri)
                && string.IsNullOrWhiteSpace(nowPlaying.Title))
                return;

            var track = _library.FindBySonosUri(nowPlaying.TrackUri);
            if (track is null)
                return;

            if (!track.SonosPlayable)
            {
                AppLog.Warn(
                    $"Now playing may be Sonos-unplayable (format): {track.Title} — {track.Artist} | " +
                    $"{track.AudioFormatLabel} | {track.SonosPlayIssue} | {track.Path}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Unplayable now-playing check failed", ex);
        }
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

    /// <summary>Tray icon double-click — Options: shuffle, open Control, or open Library.</summary>
    private void OnTrayDoubleClick()
    {
        var action = _settings.EnsureShape().TrayDoubleClickAction;
        if (string.Equals(action, AppSettings.TrayDoubleClickControl, StringComparison.OrdinalIgnoreCase))
        {
            ShowMainWindowTab("control");
            return;
        }

        if (string.Equals(action, AppSettings.TrayDoubleClickLibrary, StringComparison.OrdinalIgnoreCase))
        {
            ShowMainWindowTab("library");
            return;
        }

        _ = ExecuteActionAsync(HotsonosAction.ShuffleLibrary);
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
            _mainWindow = new MainWindow(_sonos, _store, _settings, _library, ApplyBindings, OnRoomChangedFromWindow,
                action => ExecuteActionAsync(action),
                mcpEndpoint: () => _mcpHost?.Endpoint ?? _mcpState?.Endpoint);
            _mainWindow.HideToTrayRequested += (_, _) => _mainWindow?.Hide();
            _mainWindow.Closing += OnMainWindowClosing;
        }

        if (!_mainWindow.IsVisible)
            _mainWindow.Show(); // IsVisibleChanged kicks off device discovery
        else
            _mainWindow.RefreshDevicesInBackground(); // already open: still re-discover

        BringMainWindowToFront();
    }

    /// <summary>
    /// Force the main window above other apps. Plain <see cref="Window.Activate"/> is often
    /// ignored by Windows when focus is elsewhere (tray click is a common case).
    /// </summary>
    private void BringMainWindowToFront()
    {
        if (_mainWindow is null)
            return;

        if (!_mainWindow.IsVisible)
            _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    /// <summary>Open main window on a specific tab (settings | library | mcp).</summary>
    private void ShowMainWindowTab(string tab)
    {
        ShowMainWindow();
        _mainWindow?.SelectTab(tab);
    }

    /// <summary>Background wait: second-instance launches set this event to surface the UI.</summary>
    private void ShowWindowListenerLoop()
    {
        var evt = _showWindowEvent;
        if (evt is null) return;

        try
        {
            while (!_isExiting)
            {
                if (!evt.WaitOne(TimeSpan.FromSeconds(1)))
                    continue;
                if (_isExiting) break;
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowMainWindow();
                        _tray?.ShowBalloon("HotSonos", "Already running — opened Settings.");
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Show-window signal failed", ex);
                }
            }
        }
        catch (ObjectDisposedException) { /* exit */ }
        catch (Exception ex)
        {
            AppLog.Warn("Show-window listener ended", ex);
        }
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
        AppLog.Lifecycle($"Exit requested (tray/menu) uptime={_uptime.Elapsed:hh\\:mm\\:ss}");

        try { _showWindowEvent?.Set(); } catch { /* exit */ }
        try { _showWindowEvent?.Dispose(); } catch { /* exit */ }
        _showWindowEvent = null;

        _nightlyTimer?.Dispose();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _pendingTagTimer?.Dispose();
        _pendingTagTimer = null;
        _wake?.Dispose();
        try { _library?.Dispose(); } catch { /* exit */ }
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
