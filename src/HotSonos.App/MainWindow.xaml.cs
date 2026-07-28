using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HotSonos.App.Infrastructure;
using HotSonos.App.Library;
using HotSonos.App.Mcp;
using HotSonos.App.Models;
using HotSonos.App.Services;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using MenuItem = System.Windows.Controls.MenuItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace HotSonos.App;

public partial class MainWindow : Window
{
    private const string NoneLabel = "(none)";

    private readonly SonosManager _sonos;
    private readonly ConfigStore _store;
    private readonly AppSettings _settings;
    private readonly LibraryService? _library;
    private readonly Func<IReadOnlyList<HotsonosAction>> _applyBindings;
    private readonly Action<string> _onRoomChanged;
    private readonly Action<HotsonosAction> _runAction;
    private readonly Func<string?> _mcpEndpoint;
    private DispatcherTimer? _libraryStatusTimer;
    private bool _mcpUiHooked;

    /// <summary>Working master mappings edited in Library paths UI (committed on Save).</summary>
    private readonly ObservableCollection<MasterLibraryMapping> _masterMappings = [];

    /// <summary>Working library folders list (path + Daily checkbox). No free-text path lists.</summary>
    private readonly ObservableCollection<LibraryFolderRow> _libraryFolders = [];

    // Working copies edited by the UI; copied back into _settings on Save.
    private readonly HotkeyConfig _levelVolumes;
    private readonly HotkeyConfig _freshStart;
    private readonly HotkeyConfig _shuffle;
    private readonly HotkeyConfig _playPause;
    private readonly HotkeyConfig _next;
    private readonly HotkeyConfig _previous;
    private readonly HotkeyConfig _volumeUp;
    private readonly HotkeyConfig _volumeDown;
    private readonly HotkeyConfig _mute;
    private readonly HotkeyConfig _quickTag;
    private readonly HotkeyConfig _quickPlay;
    private readonly HotkeyConfig[] _favHotkeys;

    private readonly Dictionary<TextBox, HotkeyConfig> _boxToConfig = [];
    private readonly Dictionary<string, (TextBox Box, HotkeyConfig Config)> _byTag = [];
    private ComboBox[] _favCombos = [];
    private bool _loadingStartupPreference;
    private bool _suppressRoomChange;
    private bool _refreshInProgress;
    private bool _loaded;
    private bool _controlPlayBusy;
    private IReadOnlyList<string> _sonosPlayableTitles = [];

    public event EventHandler? HideToTrayRequested;

    public MainWindow(
        SonosManager sonos,
        ConfigStore store,
        AppSettings settings,
        LibraryService? library,
        Func<IReadOnlyList<HotsonosAction>> applyBindings,
        Action<string> onRoomChanged,
        Action<HotsonosAction> runAction,
        Func<string?>? mcpEndpoint = null)
    {
        _sonos = sonos;
        _store = store;
        _settings = settings.EnsureShape();
        _library = library;
        _applyBindings = applyBindings;
        _onRoomChanged = onRoomChanged;
        _runAction = runAction;
        _mcpEndpoint = mcpEndpoint ?? (() => null);

        _levelVolumes = Clone(_settings.LevelVolumes);
        _freshStart = Clone(_settings.FreshStart);
        _shuffle = Clone(_settings.ShuffleLibrary);
        _playPause = Clone(_settings.PlayPause);
        _next = Clone(_settings.Next);
        _previous = Clone(_settings.Previous);
        _volumeUp = Clone(_settings.VolumeUp);
        _volumeDown = Clone(_settings.VolumeDown);
        _mute = Clone(_settings.Mute);
        _quickTag = Clone(_settings.QuickTag);
        _quickPlay = Clone(_settings.QuickPlay);
        _favHotkeys = _settings.FavoriteSlots.Select(s => Clone(s.Hotkey)).ToArray();

        InitializeComponent();
        Title = AppVersion.DisplayName;
        RestoreWindowGeometry();
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        Closed += OnClosed;
        _sonos.NowPlayingChanged += OnControlNowPlayingChanged;
    }

    /// <summary>Select Settings, Library, Tags, or MCP Debug tab by name.</summary>
    public void SelectTab(string tab)
    {
        if (string.Equals(tab, "library", StringComparison.OrdinalIgnoreCase))
            MainTabs.SelectedItem = LibraryTab;
        else if (string.Equals(tab, "control", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(tab, "settings", StringComparison.OrdinalIgnoreCase))
            MainTabs.SelectedItem = ControlTab;
        else if (string.Equals(tab, "tags", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(tab, "tag", StringComparison.OrdinalIgnoreCase))
            MainTabs.SelectedItem = TagsTab;
        else if (string.Equals(tab, "mcp", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(tab, "mcp debug", StringComparison.OrdinalIgnoreCase))
            MainTabs.SelectedItem = McpTab;
        else if (string.Equals(tab, "shuffle", StringComparison.OrdinalIgnoreCase))
            MainTabs.SelectedItem = MainTabs.Items.OfType<TabItem>()
                .FirstOrDefault(t => string.Equals(t.Header?.ToString(), "Shuffle", StringComparison.OrdinalIgnoreCase))
                ?? MainTabs.Items[2] as TabItem;
        else if (string.Equals(tab, "hotkeys", StringComparison.OrdinalIgnoreCase))
            MainTabs.SelectedItem = MainTabs.Items.OfType<TabItem>()
                .FirstOrDefault(t => string.Equals(t.Header?.ToString(), "Hotkeys", StringComparison.OrdinalIgnoreCase))
                ?? MainTabs.Items[1] as TabItem;
        else if (string.Equals(tab, "wake", StringComparison.OrdinalIgnoreCase))
            MainTabs.SelectedItem = MainTabs.Items.OfType<TabItem>()
                .FirstOrDefault(t => string.Equals(t.Header?.ToString(), "Wake", StringComparison.OrdinalIgnoreCase));
        else if (string.Equals(tab, "options", StringComparison.OrdinalIgnoreCase))
            MainTabs.SelectedItem = MainTabs.Items.OfType<TabItem>()
                .FirstOrDefault(t => string.Equals(t.Header?.ToString(), "Options", StringComparison.OrdinalIgnoreCase));
        else
            MainTabs.SelectedItem = ControlTab; // Control / Settings default
    }

    /// <summary>Applies the last saved position/size, if any; otherwise keeps the XAML defaults.</summary>
    private void RestoreWindowGeometry()
    {
        if (_settings.MainWindowWidth is double w && w > 300)
            Width = w;
        if (_settings.MainWindowHeight is double h && h > 300)
            Height = h;
        if (_settings.MainWindowLeft is { } left && _settings.MainWindowTop is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    /// <summary>Captures the current position/size (while not minimized/maximized) for next launch.</summary>
    private void CaptureWindowGeometry()
    {
        if (WindowState != WindowState.Normal)
            return;

        _settings.MainWindowLeft = Left;
        _settings.MainWindowTop = Top;
        _settings.MainWindowWidth = Width;
        _settings.MainWindowHeight = Height;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Window is usually cancelled into a Hide-to-tray; still persist edits.
        CaptureWindowGeometry();
        try
        {
            CommitWorkingValuesToSettings();
            _store.Save(_settings);
            _ = _applyBindings();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Settings window close save failed", ex);
        }
        base.OnClosing(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _favCombos = [Fav1NameCombo, Fav2NameCombo, Fav3NameCombo, Fav4NameCombo, Fav5NameCombo, Fav6NameCombo];

        _boxToConfig[LevelVolumesHotkeyBox] = _levelVolumes;
        _boxToConfig[FreshStartHotkeyBox] = _freshStart;
        _boxToConfig[ShuffleHotkeyBox] = _shuffle;
        _boxToConfig[PlayPauseHotkeyBox] = _playPause;
        _boxToConfig[NextHotkeyBox] = _next;
        _boxToConfig[PreviousHotkeyBox] = _previous;
        _boxToConfig[VolumeUpHotkeyBox] = _volumeUp;
        _boxToConfig[VolumeDownHotkeyBox] = _volumeDown;
        _boxToConfig[MuteHotkeyBox] = _mute;
        _boxToConfig[QuickTagHotkeyBox] = _quickTag;
        _boxToConfig[QuickPlayHotkeyBox] = _quickPlay;
        _boxToConfig[Fav1HotkeyBox] = _favHotkeys[0];
        _boxToConfig[Fav2HotkeyBox] = _favHotkeys[1];
        _boxToConfig[Fav3HotkeyBox] = _favHotkeys[2];
        _boxToConfig[Fav4HotkeyBox] = _favHotkeys[3];
        _boxToConfig[Fav5HotkeyBox] = _favHotkeys[4];
        _boxToConfig[Fav6HotkeyBox] = _favHotkeys[5];

        _byTag["LevelVolumes"] = (LevelVolumesHotkeyBox, _levelVolumes);
        _byTag["FreshStart"] = (FreshStartHotkeyBox, _freshStart);
        _byTag["Shuffle"] = (ShuffleHotkeyBox, _shuffle);
        _byTag["PlayPause"] = (PlayPauseHotkeyBox, _playPause);
        _byTag["Next"] = (NextHotkeyBox, _next);
        _byTag["Previous"] = (PreviousHotkeyBox, _previous);
        _byTag["VolumeUp"] = (VolumeUpHotkeyBox, _volumeUp);
        _byTag["VolumeDown"] = (VolumeDownHotkeyBox, _volumeDown);
        _byTag["Mute"] = (MuteHotkeyBox, _mute);
        _byTag["QuickTag"] = (QuickTagHotkeyBox, _quickTag);
        _byTag["QuickPlay"] = (QuickPlayHotkeyBox, _quickPlay);
        _byTag["Fav1"] = (Fav1HotkeyBox, _favHotkeys[0]);
        _byTag["Fav2"] = (Fav2HotkeyBox, _favHotkeys[1]);
        _byTag["Fav3"] = (Fav3HotkeyBox, _favHotkeys[2]);
        _byTag["Fav4"] = (Fav4HotkeyBox, _favHotkeys[3]);
        _byTag["Fav5"] = (Fav5HotkeyBox, _favHotkeys[4]);
        _byTag["Fav6"] = (Fav6HotkeyBox, _favHotkeys[5]);

        foreach (var (box, cfg) in _boxToConfig)
            box.Text = cfg.ToString();

        FlyoutOnTrackChangeCheckBox.IsChecked = _settings.ShowFlyoutOnTrackChange;
        FlyoutOnActionCheckBox.IsChecked = _settings.ShowFlyoutOnAction;
        LoadTrayDoubleClickCombo();
        VolumeStepBox.Text = _settings.VolumeStep.ToString();
        LevelPercentBox.Text = _settings.LevelVolumePercent.ToString();
        NightlyResetCheckBox.IsChecked = _settings.NightlyResetEnabled;
        NightlyResetTimeBox.Text = MinutesToHhmm(_settings.NightlyResetMinutes);
        NightlyResetReshuffleCheckBox.IsChecked = _settings.NightlyResetReshuffle;
        McpEnabledCheckBox.IsChecked = _settings.McpEnabled;
        McpPortBox.Text = _settings.McpPort.ToString();
        ShuffleQueueTracksBox.Text = _settings.ShuffleQueueTracks.ToString();
        ShuffleTopUpTracksBox.Text = _settings.ShuffleTopUpTracks.ToString();
        ShuffleHistoryDaysBox.Text = _settings.ShuffleHistoryDays.ToString();
        ShuffleTopUpRemainingBox.Text = _settings.ShuffleTopUpWhenRemaining.ToString();
        ShuffleExcludePlayedCheckBox.IsChecked = _settings.ShuffleExcludePlayed;
        ShuffleAutoTopUpCheckBox.IsChecked = _settings.ShuffleAutoTopUp;
        ContinueShuffleAfterSpecialPlayCheckBox.IsChecked = _settings.ContinueLibraryShuffleAfterSpecialPlay;
        ShuffleArtistSpreadCheckBox.IsChecked = _settings.ShuffleArtistSpread;
        ShowGenresInPlaySourcesCheckBox.IsChecked = _settings.ShowGenresInPlaySources;
        RefreshPlayHistoryStatus();
        LoadMasterMappingsUi(_settings.MasterLibraryMappings);
        if (MasterMappingsList is not null)
            MasterMappingsList.ItemsSource = _masterMappings;
        if (LibraryFoldersList is not null)
            LibraryFoldersList.ItemsSource = _libraryFolders;
        RebuildLibraryFoldersUi(preserveDailyChecks: false);
        RefreshMasterMapSonosCombo();
        LoadWakeUiFromSettings();
        LoadStartupPreference();
        RefreshLibraryStatusUi();
        RebuildLibraryPresetButtons();
        RefreshTagsCatalogGrid();
        StartLibraryStatusTimer();
        HookMcpActivityUi();
        RefreshMcpEndpointUi();
        RefreshMcpActivityList(scrollToEnd: true);

        PopulateRooms();
        _ = LoadFavoritesAsync();
        _ = LoadSpeakerVolumesAsync();
        RefreshControlShuffleSourceCombo();
        RefreshControlPlayList();
        ApplyControlNowPlaying(null);
        _loaded = true;

        // First open: full discovery in the background (same as every subsequent show).
        RefreshDevicesInBackground();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _sonos.NowPlayingChanged -= OnControlNowPlayingChanged;
        if (_mcpUiHooked)
        {
            McpActivityLog.Changed -= OnMcpActivityChanged;
            McpActivityLog.LibrarySearchPublished -= OnLibrarySearchFromMcp;
            _mcpUiHooked = false;
        }
        _libraryStatusTimer?.Stop();
    }

    private static readonly HttpClient ControlArtHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    private HotSonos.Core.Models.NowPlaying? _controlNowPlaying;
    private int _controlArtGeneration;

    private void OnControlNowPlayingChanged(HotSonos.Core.Models.NowPlaying np) =>
        Dispatcher.InvokeAsync(() => ApplyControlNowPlaying(np));

    private void ApplyControlNowPlaying(HotSonos.Core.Models.NowPlaying? np)
    {
        _controlNowPlaying = np;
        if (ControlNowPlayingTitle is null)
            return;

        if (np is null || np.IsEmpty)
        {
            ControlNowPlayingTitle.Text = "Nothing playing";
            ControlNowPlayingArtist.Text = "";
            ControlNowPlayingState.Text = "";
            if (ControlTransportPlayPauseButton is not null)
                ControlTransportPlayPauseButton.Content = "▶";
            SetControlNowPlayingArt(null);
            return;
        }

        ControlNowPlayingTitle.Text = string.IsNullOrWhiteSpace(np.Title) ? "(unknown title)" : np.Title!;
        ControlNowPlayingArtist.Text = string.IsNullOrWhiteSpace(np.Artist)
            ? (np.Album ?? "")
            : (string.IsNullOrWhiteSpace(np.Album) ? np.Artist! : $"{np.Artist} — {np.Album}");
        ControlNowPlayingState.Text = np.State.ToString();
        if (ControlTransportPlayPauseButton is not null)
        {
            ControlTransportPlayPauseButton.Content =
                np.State is HotSonos.Core.Models.SonosTransportState.Playing
                    or HotSonos.Core.Models.SonosTransportState.Transitioning
                    ? "⏸"
                    : "▶";
        }

        SetControlNowPlayingArt(np.AlbumArtUri);
    }

    /// <summary>
    /// Load album art the same way as the flyout: fetch bytes ourselves so URI failures
    /// never crash the UI thread.
    /// </summary>
    private async void SetControlNowPlayingArt(string? uri)
    {
        var generation = ++_controlArtGeneration;
        if (ControlNowPlayingArt is null)
            return;

        if (string.IsNullOrWhiteSpace(uri))
        {
            ControlNowPlayingArt.Source = null;
            return;
        }

        try
        {
            var bytes = await ControlArtHttp.GetByteArrayAsync(uri).ConfigureAwait(true);
            if (generation != _controlArtGeneration)
                return;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 144; // 72dp @ 2x
            bmp.EndInit();
            bmp.Freeze();
            ControlNowPlayingArt.Source = bmp;
        }
        catch (Exception ex)
        {
            if (generation == _controlArtGeneration)
            {
                ControlNowPlayingArt.Source = null;
                AppLog.Warn($"Control album art load failed ({uri})", ex);
            }
        }
    }

    private void ControlTransportPlayPause_Click(object sender, RoutedEventArgs e) =>
        _runAction(HotsonosAction.PlayPause);

    private void ControlTransportNext_Click(object sender, RoutedEventArgs e) =>
        _runAction(HotsonosAction.Next);

    private void ControlTransportPrevious_Click(object sender, RoutedEventArgs e) =>
        _runAction(HotsonosAction.Previous);

    private void ControlTransportVolUp_Click(object sender, RoutedEventArgs e) =>
        _runAction(HotsonosAction.VolumeUp);

    private void ControlTransportVolDown_Click(object sender, RoutedEventArgs e) =>
        _runAction(HotsonosAction.VolumeDown);

    private void ControlTransportMute_Click(object sender, RoutedEventArgs e) =>
        _runAction(HotsonosAction.Mute);

    private void ControlDeleteNowPlaying_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null)
        {
            SetStatus("Library service not available.", warn: true);
            return;
        }

        var np = _controlNowPlaying;
        if (np is null || string.IsNullOrWhiteSpace(np.TrackUri))
        {
            MessageBox.Show(this, "Nothing is playing (or no track URI yet).",
                "Delete track", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var track = _library.FindBySonosUri(np.TrackUri) ?? _library.GetTrack(np.TrackUri);
        var path = track?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this,
                "Could not match the playing track to a library file path.\n\n" +
                "Rescan the library, or delete from the Library tab after search.",
                "Delete track",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var title = string.IsNullOrWhiteSpace(np.Title)
            ? (track?.Title ?? System.IO.Path.GetFileName(path))
            : np.Title;
        var artist = np.Artist ?? track?.Artist ?? "";

        var body =
            "PERMANENTLY DELETE the currently playing track from disk?\n\n" +
            $"• {title}" + (string.IsNullOrWhiteSpace(artist) ? "" : $" — {artist}") + "\n" +
            $"{path}\n\n" +
            "This cannot be undone.\n\n" +
            "• Sonos library file will be deleted\n" +
            "• Linked or matched master/hi-res twin will also be deleted when a master mapping applies\n\n" +
            "Continue?";

        var confirm = MessageBox.Show(
            this, body, "Confirm permanent delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        var confirm2 = MessageBox.Show(
            this,
            "Last chance: delete this track from disk (Sonos + master when linked)?\n\nPress No to cancel.",
            "Final confirmation",
            MessageBoxButton.YesNo, MessageBoxImage.Stop, MessageBoxResult.No);
        if (confirm2 != MessageBoxResult.Yes)
            return;

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                // Skip past this track so Sonos isn't holding the file open as hard.
                try { await _sonos.ExecuteAsync(HotsonosAction.Next, _settings).ConfigureAwait(true); }
                catch (Exception ex) { AppLog.Warn("Next before delete failed (continuing)", ex); }

                var result = _library.DeleteTrack(path, deleteMaster: true);
                SetStatus(result.Message, warn: !result.Ok);
                if (result.Ok || result.SonosDeleted)
                {
                    // Drop from library grid if visible
                    if (_libraryRows is not null)
                    {
                        for (var i = _libraryRows.Count - 1; i >= 0; i--)
                        {
                            if (string.Equals(_libraryRows[i].Path, path, StringComparison.OrdinalIgnoreCase))
                                _libraryRows.RemoveAt(i);
                        }
                    }
                    RefreshLibraryStatusUi();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Control delete now-playing failed", ex);
                SetStatus(ex.Message, warn: true);
            }
        }), DispatcherPriority.Background);
    }

    private void HookMcpActivityUi()
    {
        if (_mcpUiHooked) return;
        McpActivityLog.BindDispatcher(Dispatcher);
        McpActivityLog.Changed += OnMcpActivityChanged;
        McpActivityLog.LibrarySearchPublished += OnLibrarySearchFromMcp;
        _mcpUiHooked = true;
    }

    private void OnMcpActivityChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            RefreshMcpActivityList(scrollToEnd: McpAutoScrollCheck.IsChecked == true);
            RefreshMcpEndpointUi();
        });

    private void OnLibrarySearchFromMcp(object? sender, LibrarySearchPublishedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ApplyMcpLibraryPayload(e.Tool, e.ResultJson));

    private void StartLibraryStatusTimer()
    {
        _libraryStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _libraryStatusTimer.Tick += (_, _) =>
        {
            RefreshLibraryStatusUi();
            if (MainTabs.SelectedItem == McpTab)
                RefreshMcpEndpointUi();
        };
        _libraryStatusTimer.Start();
    }

    private void RefreshLibraryStatusUi()
    {
        if (_library is null)
        {
            var missing = "Library cache: service not available.";
            LibraryStatusText.Text = missing;
            LibTabStatusText.Text = missing;
            return;
        }

        var st = _library.GetStatus();
        string line;
        if (st.IsScanning)
        {
            line =
                $"Scanning… {st.Phase ?? ""}  |  tracks in cache: {st.TrackCount}  |  last seen {st.LastScanFilesSeen}, updated {st.LastScanFilesUpdated}";
            if (LibraryRescanButton is not null)
                LibraryRescanButton.IsEnabled = false;
        }
        else
        {
            if (LibraryRescanButton is not null)
                LibraryRescanButton.IsEnabled = true;
            var last = st.LastScanFinishedUtc is { } t
                ? t.ToLocalTime().ToString("g")
                : "never";
            var err = string.IsNullOrWhiteSpace(st.LastScanError) ? "" : $"  |  error: {st.LastScanError}";
            line =
                $"Cache: {st.TrackCount} track(s)  |  roots: {st.RootsConfigured}  |  last scan: {last}  |  updated {st.LastScanFilesUpdated}, skipped {st.LastScanFilesSkippedUnchanged}, removed {st.LastScanFilesRemoved}{err}";
        }

        if (st.SonosUnplayableCount > 0)
            line += $"  |  ⚠ Sonos-unplayable (format): {st.SonosUnplayableCount}";
        if (_library?.NeedsAudioPropsRescan() == true)
            line += "  |  re-scan needed for bitrates (Force re-read tags)";

        LibraryStatusText.Text = line;
        LibTabStatusText.Text = line + (string.IsNullOrWhiteSpace(st.DatabasePath) ? "" : $"\nDB: {st.DatabasePath}");
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        // SelectionChanged bubbles from every ComboBox/DataGrid inside tabs.
        // Only react when the left TabControl itself changed pages — otherwise
        // e.g. TagsCatalogGrid selection re-enters RefreshTagsCatalogGrid forever
        // (System.StackOverflowException / process dies, tray gone).
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
            return;

        if (MainTabs.SelectedItem == McpTab)
        {
            RefreshMcpEndpointUi();
            RefreshMcpActivityList(scrollToEnd: false);
        }
        else if (MainTabs.SelectedItem == LibraryTab)
            RefreshLibraryStatusUi();
        else if (MainTabs.SelectedItem == TagsTab)
            RefreshTagsCatalogGrid();
        else if (MainTabs.SelectedItem == ControlTab)
        {
            RefreshControlShuffleSourceCombo();
            RefreshControlPlayList();
        }
    }

    private void RefreshMcpEndpointUi()
    {
        var ep = _mcpEndpoint();
        var enabled = _settings.McpEnabled;
        if (!string.IsNullOrWhiteSpace(ep))
            McpEndpointText.Text = $"Endpoint: {ep}  ·  listening";
        else if (enabled)
            McpEndpointText.Text = $"Endpoint: http://127.0.0.1:{_settings.McpPort}/mcp  ·  starting or not bound yet";
        else
            McpEndpointText.Text = "MCP disabled in Settings (enable + restart if needed).";
    }

    private void RefreshMcpActivityList(bool scrollToEnd)
    {
        var snap = McpActivityLog.Snapshot();
        var selected = McpActivityList.SelectedItem as McpActivityEntry;
        McpActivityList.ItemsSource = snap;
        if (selected is not null)
        {
            var match = snap.FirstOrDefault(e =>
                e.TimeLocal == selected.TimeLocal && e.Tool == selected.Tool && e.DurationMs == selected.DurationMs);
            if (match is not null)
                McpActivityList.SelectedItem = match;
        }

        if (scrollToEnd && snap.Count > 0)
        {
            McpActivityList.SelectedItem = snap[^1];
            McpActivityList.ScrollIntoView(snap[^1]);
            McpDetailBox.Text = snap[^1].DetailText;
        }
    }

    private void McpActivityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (McpActivityList.SelectedItem is McpActivityEntry entry)
            McpDetailBox.Text = entry.DetailText;
    }

    private void McpClearLog_Click(object sender, RoutedEventArgs e)
    {
        McpActivityLog.Clear();
        McpDetailBox.Text = "";
        RefreshMcpActivityList(scrollToEnd: false);
    }

    private void McpCopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var ep = _mcpEndpoint() ?? $"http://127.0.0.1:{_settings.McpPort}/mcp";
        try
        {
            System.Windows.Clipboard.SetText(ep);
            SetStatus($"Copied {ep}", warn: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard failed: {ex.Message}", warn: true);
        }
    }

    private void LibrarySearchButton_Click(object sender, RoutedEventArgs e) =>
        RunLibrarySearch(LibrarySearchBox.Text, browse: false);

    private void LibraryBrowseButton_Click(object sender, RoutedEventArgs e) =>
        RunLibrarySearch(null, browse: true);

    private void LibrarySearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            RunLibrarySearch(LibrarySearchBox.Text, browse: false);
        }
    }

    private void LibraryUnplayableButton_Click(object sender, RoutedEventArgs e)
    {
        LibraryUnplayableOnlyCheck.IsChecked = true;
        RunLibrarySearch(LibrarySearchBox.Text, browse: true);
    }

    private void ClearPlayHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitWorkingValuesToSettings();
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Save before clear history failed", ex);
        }

        var removed = _sonos.PlayHistory.Clear();
        RefreshPlayHistoryStatus();
        SetStatus(
            removed == 0
                ? "Play history was already empty."
                : $"Cleared {removed} history entries. Shuffle again to build a fresh queue.",
            warn: false);
    }

    private void RefreshPlayHistoryStatus()
    {
        try
        {
            var n = _sonos.PlayHistory.PlayedDistinctCount;
            var days = _settings.ShuffleHistoryDays;
            PlayHistoryStatusText.Text = $"History: {n} distinct played track(s) (last {days} days)";
        }
        catch
        {
            PlayHistoryStatusText.Text = "History: (unavailable)";
        }
    }

    private bool _libraryTempoUiBusy;
    private bool _libraryTempoUiSyncing;
    /// <summary>Live library grid rows — mutated in place so tag updates don't rebind/jump selection.</summary>
    private ObservableCollection<LibraryResultRow>? _libraryRows;

    private void LibraryResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SyncLibraryPresetButtonsFromSelection();

    /// <summary>Build toggle buttons from the flat tag catalog (labels only; keys stay internal).</summary>
    private void RebuildLibraryPresetButtons()
    {
        LibraryPresetButtonsHost.Children.Clear();
        var tags = _settings.EnsureShape().Tags.ToList();
        for (var i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            var digitHint = i < 9 ? $"{i + 1}  " : "";
            var btn = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = $"{digitHint}{t.Label}",
                Tag = t.Key,
                MinWidth = 72,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(10, 0, 10, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = i < 9
                    ? $"Toggle “{t.Label}” (key {i + 1}). Select-all on multi-select."
                    : $"Toggle “{t.Label}”. Select-all on multi-select.",
                Focusable = false,
            };
            btn.Click += LibraryTagButton_Click;
            LibraryPresetButtonsHost.Children.Add(btn);
        }

        SyncLibraryPresetButtonsFromSelection();
    }

    /// <summary>
    /// Lit = every selected track has that tag key. Multi: select-all style (not invert).
    /// </summary>
    private void SyncLibraryPresetButtonsFromSelection()
    {
        if (_libraryTempoUiSyncing)
            return;

        var rows = LibraryResultsGrid.SelectedItems.OfType<LibraryResultRow>().ToList();
        _libraryTempoUiSyncing = true;
        try
        {
            var tracks = new List<LibraryTrack?>();
            if (_library is not null)
            {
                foreach (var r in rows)
                {
                    if (string.IsNullOrWhiteSpace(r.Path))
                    {
                        tracks.Add(null);
                        continue;
                    }

                    tracks.Add(_library.GetTrack(r.Path!) ?? _library.FindBySonosUri(r.Path));
                }
            }

            foreach (var child in LibraryPresetButtonsHost.Children)
            {
                if (child is not System.Windows.Controls.Primitives.ToggleButton { Tag: string key } btn)
                    continue;
                if (rows.Count == 0 || tracks.Count == 0)
                {
                    btn.IsChecked = false;
                    continue;
                }

                btn.IsChecked = tracks.All(t => t is not null && t.HasTagKey(key));
            }

            if (rows.Count == 0)
            {
                LibraryTempoSelectionHint.Text =
                    "Select track(s) — click tags (or keys 1–9 for first nine). Lit = all selected have that tag.";
            }
            else if (rows.Count == 1)
            {
                var t = tracks.FirstOrDefault();
                LibraryTempoSelectionHint.Text = t is null
                    ? "Selected row not in cache — rescan?"
                    : LibraryService.FormatCurrentTags(t, _settings);
            }
            else
            {
                LibraryTempoSelectionHint.Text =
                    $"{rows.Count} selected — lit = all have tag; click turns on for all (or off if all already have it)";
            }
        }
        finally
        {
            _libraryTempoUiSyncing = false;
        }
    }

    private void LibraryTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string key })
        {
            SyncLibraryPresetButtonsFromSelection();
            return;
        }

        ApplyLibraryTagToSelection(key);
    }

    /// <summary>
    /// Keys 1–9 toggle the first nine catalog tags when Library tab is active.
    /// </summary>
    private void LibraryTab_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBox)
            return;

        var index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            _ => -1,
        };
        if (index < 0)
            return;
        var tags = _settings.EnsureShape().Tags;
        if (index >= tags.Count)
            return;

        e.Handled = true;
        ApplyLibraryTagToSelection(tags[index].Key);
    }

    /// <summary>Select-all style toggle for one tag key on the grid selection.</summary>
    private void ApplyLibraryTagToSelection(string tagKey)
    {
        if (_libraryTempoUiBusy)
            return;

        if (_library is null)
        {
            SetStatus("Library service not available.", warn: true);
            SyncLibraryPresetButtonsFromSelection();
            return;
        }

        var def = _settings.EnsureShape().FindTag(tagKey);
        if (def is null)
        {
            SetStatus("Unknown tag.", warn: true);
            return;
        }

        var rows = LibraryResultsGrid.SelectedItems.OfType<LibraryResultRow>()
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .ToList();
        if (rows.Count == 0)
        {
            SetStatus("Select one or more tracks first.", warn: true);
            SyncLibraryPresetButtonsFromSelection();
            return;
        }

        // Select-all: if every selected already has tag → turn OFF for all; else turn ON for all.
        var tracks = rows
            .Select(r => _library.GetTrack(r.Path!) ?? _library.FindBySonosUri(r.Path))
            .ToList();
        var allHave = tracks.Count > 0 && tracks.All(t => t is not null && t.HasTagKey(tagKey));
        var turnOn = !allHave;
        var verb = turnOn ? "on" : "off";

        KeepLibraryGridKeyboardFocus();
        var heavy = rows.Count > 1;
        SetLibraryTagBusy(true, $"“{def.Label}” {verb} ({rows.Count})…", heavy);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var ok = 0;
                var fail = 0;
                var queued = 0;
                string? lastMsg = null;
                var updated = new Dictionary<string, LibraryTrack>(StringComparer.OrdinalIgnoreCase);
                var total = rows.Count;
                var i = 0;

                foreach (var row in rows)
                {
                    i++;
                    if (heavy)
                        SetLibraryBusyProgress(i, total, $"“{def.Label}” {verb} — {i} of {total}…");
                    else
                        LibraryTempoActionFeedback.Text = "Working…";

                    var result = _library.SetTagFlag(
                        row.Path!, tagKey, forceEnable: turnOn,
                        dryRun: false, updateMaster: _settings.TagUpdateMasterDefault);
                    if (result.Ok)
                    {
                        ok++;
                        if (result.Queued) queued++;
                        lastMsg = result.Message;
                        var after = result.TrackAfter ?? (result.Queued ? null : _library.GetTrack(row.Path!));
                        if (after is not null)
                            updated[row.Path!] = after;
                    }
                    else
                    {
                        fail++;
                        lastMsg = result.Error ?? result.Message;
                    }
                }

                if (updated.Count > 0)
                    PatchLibraryGridRows(updated);

                var msg = fail == 0
                    ? (queued > 0
                        ? $"“{def.Label}” {verb}: {ok} ok ({queued} queued). {lastMsg}"
                        : $"“{def.Label}” {verb}: updated {ok}. {lastMsg}")
                    : $"“{def.Label}” {verb}: {ok} ok, {fail} failed. {lastMsg}";
                SetStatus(msg, warn: fail > 0);
                LibraryTempoActionFeedback.Text = fail == 0
                    ? (queued > 0 ? $"Queued {queued}/{ok}" : $"Done ({ok})")
                    : $"Failed {fail}";
                LibraryTempoActionFeedback.Foreground = fail > 0
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x70, 0x20))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1C, 0x8E, 0x54));
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, warn: true);
                LibraryTempoActionFeedback.Text = "Error";
            }
            finally
            {
                SetLibraryTagBusy(false, null, heavy: false);
                SyncLibraryPresetButtonsFromSelection();
                KeepLibraryGridKeyboardFocus();
            }
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// Busy chrome. Light = status text only; heavy = multi progress banner.
    /// Never disables the grid (keeps selection + arrow keys stable).
    /// </summary>
    private void SetLibraryTagBusy(bool busy, string? message, bool heavy)
    {
        _libraryTempoUiBusy = busy;
        foreach (var child in LibraryPresetButtonsHost.Children)
        {
            if (child is System.Windows.Controls.Primitives.ToggleButton btn)
                btn.IsHitTestVisible = !busy;
        }

        if (busy)
        {
            LibraryTempoActionFeedback.Text = message ?? "Working…";
            LibraryTempoActionFeedback.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1C, 0x8E, 0x54));
            SetStatus(message ?? "Working…", warn: false);

            if (heavy)
            {
                LibraryBusyBanner.Visibility = Visibility.Visible;
                LibraryBusyProgress.IsIndeterminate = true;
                LibraryBusyBannerText.Text = message ?? "Working…";
            }
            else
            {
                LibraryBusyBanner.Visibility = Visibility.Collapsed;
            }

            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
        else
        {
            LibraryBusyBanner.Visibility = Visibility.Collapsed;
            LibraryBusyProgress.IsIndeterminate = false;
            LibraryBusyBannerText.Text = "";
            Mouse.OverrideCursor = null;
        }
    }

    private void SetLibraryBusyProgress(int current, int total, string message)
    {
        LibraryBusyBanner.Visibility = Visibility.Visible;
        LibraryBusyProgress.IsIndeterminate = false;
        LibraryBusyProgress.Minimum = 0;
        LibraryBusyProgress.Maximum = Math.Max(1, total);
        LibraryBusyProgress.Value = current;
        LibraryBusyBannerText.Text = message;
        LibraryTempoActionFeedback.Text = $"{current}/{total}";
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    /// <summary>Put keyboard focus on the grid without changing which row is selected.</summary>
    private void KeepLibraryGridKeyboardFocus()
    {
        try
        {
            if (LibraryResultsGrid.IsKeyboardFocusWithin)
                return;
            LibraryResultsGrid.Focus();
            Keyboard.Focus(LibraryResultsGrid);
        }
        catch { /* ignore */ }
    }

    private void RunLibrarySearch(string? query, bool browse)
    {
        if (_library is null)
        {
            SetStatus("Library service not available.", warn: true);
            return;
        }

        var q = browse || string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var unplayableOnly = LibraryUnplayableOnlyCheck.IsChecked == true;
        var tracks = _library.Search(q, limit: 100, offset: 0, sonosUnplayableOnly: unplayableOnly);
        _libraryRows = new ObservableCollection<LibraryResultRow>(tracks.Select(ToLibraryResultRow));
        LibraryResultsGrid.ItemsSource = _libraryRows;
        var st = _library.GetStatus();
        var filter = unplayableOnly ? " [Sonos-unplayable only]" : "";
        var (field, _) = LibrarySearchQuery.Parse(q);
        var mode = field switch
        {
            LibrarySearchField.Title => " [title]",
            LibrarySearchField.Artist => " [artist]",
            LibrarySearchField.Tags => " [tags]",
            LibrarySearchField.Format => " [format]",
            _ => "",
        };
        LibraryResultsMetaText.Text = q is null
            ? $"Browse{filter}: {tracks.Count} shown · cache {st.TrackCount} · unplayable {st.SonosUnplayableCount}."
            : $"Search “{q}”{mode}{filter}: {tracks.Count} hit(s) · cache {st.TrackCount} · unplayable {st.SonosUnplayableCount}.";
        LibraryMcpResultBox.Visibility = Visibility.Collapsed;
        SetStatus(LibraryResultsMetaText.Text, warn: false);
        SyncLibraryPresetButtonsFromSelection();
    }

    private void ApplyMcpLibraryPayload(string tool, string? json)
    {
        LibraryMcpResultBox.Visibility = Visibility.Visible;
        LibraryMcpResultBox.Text = json ?? "";
        LibraryResultsMetaText.Text = $"Last MCP library tool: {tool} @ {DateTime.Now:T}";

        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("tracks", out var tracksEl) && tracksEl.ValueKind == JsonValueKind.Array)
            {
                var rows = new ObservableCollection<LibraryResultRow>();
                foreach (var t in tracksEl.EnumerateArray())
                    rows.Add(LibraryResultRow.FromJson(t, k => _settings.TagLabel(k)));
                _libraryRows = rows;
                LibraryResultsGrid.ItemsSource = _libraryRows;
                LibraryResultsMetaText.Text =
                    $"MCP {tool}: {rows.Count} track row(s) · {DateTime.Now:T}";
            }
            else if (root.TryGetProperty("track", out var one) && one.ValueKind == JsonValueKind.Object)
            {
                _libraryRows = new ObservableCollection<LibraryResultRow>
                {
                    LibraryResultRow.FromJson(one, k => _settings.TagLabel(k)),
                };
                LibraryResultsGrid.ItemsSource = _libraryRows;
            }

            // Soft-hint: if user is on Settings, status line still updates; switch optional.
            if (MainTabs.SelectedItem != LibraryTab && tool is "library_search" or "library_get_track")
                SetStatus($"MCP {tool} → see Library tab for results", warn: false);
        }
        catch
        {
            // JSON parse soft-fail; raw box still shows payload.
        }
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private void LibraryContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        LibraryApplyPresetMenu.Items.Clear();
        var tags = _settings.EnsureShape().Tags.ToList();
        if (tags.Count == 0)
        {
            LibraryApplyPresetMenu.Items.Add(new MenuItem { Header = "(no tags)", IsEnabled = false });
            return;
        }

        foreach (var t in tags)
        {
            var item = new MenuItem
            {
                Header = t.Label,
                Tag = t.Key,
            };
            item.Click += LibraryApplyTagMenuItem_Click;
            LibraryApplyPresetMenu.Items.Add(item);
        }
    }

    private void LibraryApplyTagMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key })
            return;
        ApplyLibraryTagToSelection(key);
    }

    private void LibraryDeleteTracks_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null)
        {
            SetStatus("Library service not available.", warn: true);
            return;
        }

        var rows = LibraryResultsGrid.SelectedItems.OfType<LibraryResultRow>()
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this,
                "Select one or more tracks in the library grid first.",
                "Delete tracks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var sample = rows.Take(8)
            .Select(r =>
            {
                var title = string.IsNullOrWhiteSpace(r.Title) ? System.IO.Path.GetFileName(r.Path) : r.Title;
                var artist = string.IsNullOrWhiteSpace(r.Artist) ? "" : $" — {r.Artist}";
                return $"• {title}{artist}";
            });
        var more = rows.Count > 8 ? $"\n… and {rows.Count - 8} more" : "";

        var body =
            "PERMANENTLY DELETE from disk?\n\n" +
            $"Tracks: {rows.Count}\n\n" +
            string.Join("\n", sample) + more + "\n\n" +
            "This cannot be undone.\n\n" +
            "• Files under your Sonos library folders will be deleted\n" +
            "• Linked or matched master/hi-res twins will also be deleted when a master mapping applies\n" +
            "• Rows are removed from the HotSonos cache\n\n" +
            "Continue?";

        var confirm = MessageBox.Show(
            this,
            body,
            "Confirm permanent delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        // Second gate — major action
        var confirm2 = MessageBox.Show(
            this,
            $"Last chance: delete {rows.Count} track(s) from disk (Sonos + master when linked)?\n\nPress No to cancel.",
            "Final confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Stop,
            MessageBoxResult.No);

        if (confirm2 != MessageBoxResult.Yes)
            return;

        SetLibraryTagBusy(true, $"Deleting {rows.Count} track(s)…", heavy: rows.Count > 1);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var ok = 0;
                var fail = 0;
                var masterHits = 0;
                string? lastMsg = null;
                var pathsToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (rows.Count > 1)
                        SetLibraryBusyProgress(i + 1, rows.Count, $"Deleting {i + 1} of {rows.Count}…");

                    var result = _library.DeleteTrack(row.Path, deleteMaster: true);
                    lastMsg = result.Message;
                    if (result.Ok || result.SonosDeleted)
                    {
                        ok++;
                        pathsToRemove.Add(row.Path);
                        if (result.MasterDeleted) masterHits++;
                    }
                    else
                    {
                        fail++;
                    }
                }

                if (_libraryRows is not null && pathsToRemove.Count > 0)
                {
                    for (var i = _libraryRows.Count - 1; i >= 0; i--)
                    {
                        if (pathsToRemove.Contains(_libraryRows[i].Path))
                            _libraryRows.RemoveAt(i);
                    }
                }

                var summary = fail == 0
                    ? $"Deleted {ok} track(s)" + (masterHits > 0 ? $" ({masterHits} with master twin)." : ".")
                    : $"Deleted {ok}, failed {fail}. {lastMsg}";
                SetStatus(summary, warn: fail > 0);
                LibraryTempoActionFeedback.Text = fail == 0 ? $"Deleted {ok}" : $"Fail {fail}";
                LibraryResultsMetaText.Text = summary;
                RefreshLibraryStatusUi();
            }
            catch (Exception ex)
            {
                AppLog.Error("Library delete failed", ex);
                SetStatus(ex.Message, warn: true);
            }
            finally
            {
                SetLibraryTagBusy(false, null, heavy: false);
                SyncLibraryPresetButtonsFromSelection();
            }
        }), DispatcherPriority.Background);
    }

    // ---- Tags catalog maintenance tab ---------------------------------------

    private ObservableCollection<TagCatalogRow> _tagCatalogRows = [];
    private bool _tagsCatalogSyncing;

    private void RefreshTagsCatalogGrid(string? selectKey = null)
    {
        _settings.EnsureShape();
        _tagsCatalogSyncing = true;
        try
        {
            var keepKey = selectKey
                          ?? (TagsCatalogGrid.SelectedItem as TagCatalogRow)?.Key;
            _tagCatalogRows = new ObservableCollection<TagCatalogRow>(
                _settings.Tags.Select((t, i) => new TagCatalogRow(i + 1, t.Key, t.Label)));
            TagsCatalogGrid.ItemsSource = _tagCatalogRows;
            if (!string.IsNullOrWhiteSpace(keepKey))
            {
                var row = _tagCatalogRows.FirstOrDefault(r =>
                    string.Equals(r.Key, keepKey, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                    TagsCatalogGrid.SelectedItem = row;
            }

            TagsCatalogStatusText.Text =
                $"{_tagCatalogRows.Count} tag(s) · edit Label in place · order drives chips & keys 1–9";
        }
        finally
        {
            _tagsCatalogSyncing = false;
        }
    }

    private bool PersistTagCatalog(string okMessage)
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            SetStatus($"Tag catalog save failed: {ex.Message}", warn: true);
            TagsCatalogStatusText.Text = $"Save failed: {ex.Message}";
            return false;
        }

        RebuildLibraryPresetButtons();
        SetStatus(okMessage, warn: false);
        return true;
    }

    private bool AddTagAndPersist(string label, out string? error)
    {
        error = null;
        var tag = _settings.AddTag(label);
        if (tag is null)
        {
            error = "Enter a non-empty label.";
            return false;
        }

        if (!PersistTagCatalog($"Tag “{tag.Label}” added."))
        {
            error = "Save failed.";
            return false;
        }

        RefreshTagsCatalogGrid(tag.Key);
        return true;
    }

    private void TagsAddButton_Click(object sender, RoutedEventArgs e)
    {
        var label = TagsNewLabelBox.Text?.Trim() ?? "";
        if (label.Length == 0)
        {
            TagsCatalogStatusText.Text = "Enter a label to add.";
            SetStatus("Enter a tag name first.", warn: true);
            return;
        }

        if (!AddTagAndPersist(label, out var err))
        {
            TagsCatalogStatusText.Text = err ?? "Could not add tag.";
            return;
        }

        TagsNewLabelBox.Text = "";
        TagsNewLabelBox.Focus();
    }

    private void TagsNewLabelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TagsAddButton_Click(sender, e);
        }
    }

    private void TagsDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagsCatalogGrid.SelectedItem is not TagCatalogRow row)
        {
            TagsCatalogStatusText.Text = "Select a tag to delete.";
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete “{row.Label}”?\n\n" +
            "This removes it from the catalog and from every library track that has it " +
            "(writes HOTSONOS_TAGS on each file; locked/playing files are queued).",
            "Delete tag",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        var key = row.Key;
        var label = row.Label;
        TagsDeleteButton.IsEnabled = false;
        TagsCatalogStatusText.Text = $"Removing “{label}” from library files…";
        SetStatus($"Deleting tag “{label}” from library…", warn: false);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                TagPurgeResult? purge = null;
                if (_library is not null)
                {
                    purge = _library.PurgeTagKey(
                        key,
                        updateMaster: _settings.TagUpdateMasterDefault,
                        progress: (done, total) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                TagsCatalogStatusText.Text =
                                    $"Removing “{label}” — {done} of {total}…";
                            });
                        });
                }

                if (!_settings.RemoveTag(key))
                {
                    TagsCatalogStatusText.Text = "Tag not found in catalog.";
                    return;
                }

                var catalogMsg = $"Tag “{label}” deleted.";
                if (purge is not null)
                    catalogMsg += " " + purge.Message;
                else
                    catalogMsg += " (library offline — catalog only.)";

                PersistTagCatalog(catalogMsg);
                TagsCatalogStatusText.Text = catalogMsg;
                RefreshTagsCatalogGrid();

                // Refresh library grid labels if rows are showing.
                if (_libraryRows is { Count: > 0 } && _library is not null)
                {
                    var updated = new Dictionary<string, LibraryTrack>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in _libraryRows)
                    {
                        if (string.IsNullOrWhiteSpace(r.Path)) continue;
                        var t = _library.GetTrack(r.Path!);
                        if (t is not null)
                            updated[r.Path!] = t;
                    }
                    if (updated.Count > 0)
                        PatchLibraryGridRows(updated);
                }
            }
            catch (Exception ex)
            {
                TagsCatalogStatusText.Text = $"Delete failed: {ex.Message}";
                SetStatus(ex.Message, warn: true);
            }
            finally
            {
                TagsDeleteButton.IsEnabled = true;
            }
        }), DispatcherPriority.Background);
    }

    private void TagsMoveUpButton_Click(object sender, RoutedEventArgs e) => MoveSelectedTag(-1);

    private void TagsMoveDownButton_Click(object sender, RoutedEventArgs e) => MoveSelectedTag(1);

    private void MoveSelectedTag(int delta)
    {
        if (TagsCatalogGrid.SelectedItem is not TagCatalogRow row)
        {
            TagsCatalogStatusText.Text = "Select a tag to reorder.";
            return;
        }

        if (!_settings.MoveTag(row.Key, delta))
        {
            TagsCatalogStatusText.Text = delta < 0 ? "Already at top." : "Already at bottom.";
            return;
        }

        PersistTagCatalog($"Moved “{row.Label}”.");
        RefreshTagsCatalogGrid(row.Key);
    }

    private void TagsCatalogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_tagsCatalogSyncing) return;
        if (TagsCatalogGrid.SelectedItem is TagCatalogRow row)
            TagsCatalogStatusText.Text = $"Selected “{row.Label}”";
    }

    private void TagsCatalogGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_tagsCatalogSyncing || e.EditAction != DataGridEditAction.Commit)
            return;
        if (e.Row.Item is not TagCatalogRow row)
            return;
        if (e.Column is not DataGridTextColumn { Header: "Label" })
            return;

        // Read the edited value from the TextBox (binding may not have pushed yet).
        var newLabel = row.Label;
        if (e.EditingElement is TextBox tb)
            newLabel = tb.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(newLabel))
        {
            e.Cancel = true;
            TagsCatalogStatusText.Text = "Label cannot be empty.";
            return;
        }

        if (string.Equals(newLabel, _settings.FindTag(row.Key)?.Label, StringComparison.Ordinal))
            return;

        if (!_settings.RenameTag(row.Key, newLabel))
        {
            e.Cancel = true;
            TagsCatalogStatusText.Text = "Rename failed.";
            return;
        }

        row.Label = newLabel;
        PersistTagCatalog($"Renamed to “{newLabel}”.");
        // Defer grid refresh so edit commit finishes.
        Dispatcher.BeginInvoke(() => RefreshTagsCatalogGrid(row.Key), DispatcherPriority.Background);
    }

    private sealed class TagCatalogRow : INotifyPropertyChanged
    {
        private string _label;

        public TagCatalogRow(int index, string key, string label)
        {
            Index = index;
            Key = key;
            _label = label;
        }

        public int Index { get; }
        public string Key { get; }
        public string HotkeyHint => Index is >= 1 and <= 9 ? Index.ToString() : "—";

        public string Label
        {
            get => _label;
            set
            {
                if (_label == value) return;
                _label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Update existing grid rows in place by path — never rebind ItemsSource
    /// (rebind jumps selection / eats arrow-key navigation).
    /// </summary>
    private void PatchLibraryGridRows(IReadOnlyDictionary<string, LibraryTrack> updatedByPath)
    {
        if (_libraryRows is null || _libraryRows.Count == 0 || updatedByPath.Count == 0)
            return;

        for (var i = 0; i < _libraryRows.Count; i++)
        {
            var path = _libraryRows[i].Path;
            if (path is null || !updatedByPath.TryGetValue(path, out var track))
                continue;
            // Replace item in ObservableCollection — selection stays on the same indices.
            _libraryRows[i] = ToLibraryResultRow(track);
        }

        SyncLibraryPresetButtonsFromSelection();
    }

    private LibraryResultRow ToLibraryResultRow(LibraryTrack t)
    {
        var labels = _settings.NormalizeTagKeys(t.TagKeys)
            .Select(k => _settings.TagLabel(k))
            .Where(l => l.Length > 0);
        return new(
            t.Title,
            t.Artist,
            t.Album,
            t.AudioFormatLabel,
            t.SonosPlayable ? "OK" : "NO",
            t.SonosPlayIssue,
            string.Join(" · ", labels),
            t.Path);
    }

    private sealed record LibraryResultRow(
        string? Title,
        string? Artist,
        string? Album,
        string? Format,
        string SonosOk,
        string? Issue,
        string? Tags,
        string Path)
    {
        public static LibraryResultRow FromJson(JsonElement t, Func<string, string>? labelForKey = null)
        {
            var playable = true;
            if (t.TryGetProperty("SonosPlayable", out var sp) || t.TryGetProperty("sonosPlayable", out sp))
            {
                if (sp.ValueKind is JsonValueKind.False) playable = false;
                else if (sp.ValueKind is JsonValueKind.True) playable = true;
                else if (sp.ValueKind is JsonValueKind.Number) playable = sp.GetInt32() != 0;
            }

            string? tags = GetStr(t, "tagsLabel") ?? GetStr(t, "Tags") ?? GetStr(t, "tags");
            if (tags is null && t.TryGetProperty("tagKeys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var k in keysEl.EnumerateArray())
                {
                    var key = k.GetString();
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    parts.Add(labelForKey?.Invoke(key) ?? key);
                }
                tags = string.Join(" · ", parts);
            }

            return new LibraryResultRow(
                GetStr(t, "Title") ?? GetStr(t, "title"),
                GetStr(t, "Artist") ?? GetStr(t, "artist"),
                GetStr(t, "Album") ?? GetStr(t, "album"),
                GetStr(t, "audio") ?? GetStr(t, "Audio") ?? GetStr(t, "Codec") ?? GetStr(t, "codec"),
                playable ? "OK" : "NO",
                GetStr(t, "SonosPlayIssue") ?? GetStr(t, "sonosPlayIssue"),
                tags,
                GetStr(t, "Path") ?? GetStr(t, "path") ?? "");
        }
    }

    private void LibraryRescanButton_Click(object sender, RoutedEventArgs e) => StartLibraryRescan(forceAll: false);

    private void LibraryForceRescanButton_Click(object sender, RoutedEventArgs e) => StartLibraryRescan(forceAll: true);

    private async void DiscoverLibraryRootsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null)
        {
            SetLibraryFeedback("Library service not available.", warn: true);
            return;
        }

        var btn = sender as System.Windows.Controls.Button;
        var prevContent = btn?.Content;
        if (btn is not null)
        {
            btn.IsEnabled = false;
            btn.Content = "Discovering…";
        }

        // Paths live in a collapsed expander — open it so the result is visible.
        if (LibraryPathsExpander is not null)
            LibraryPathsExpander.IsExpanded = true;

        SetLibraryFeedback("Discovering Music Library roots from Sonos (browsing A:TRACKS — may take a minute)…", warn: false);
        try
        {
            var (ok, message, roots) = await _library.DiscoverRootsFromSonosAsync().ConfigureAwait(true);
            // Settings object already updated by discover; refresh UI list (Daily defaults applied in EnsureShape).
            _settings.EnsureShape();
            RebuildLibraryFoldersUi(preserveDailyChecks: false);
            RefreshMasterMapSonosCombo();

            var daily = SnapshotDailyLibraryRoots();
            if (daily.Count == 0)
                daily = _settings.GetEffectiveDailyLibraryRoots().ToList();
            var dailyLine = daily.Count == 0
                ? "Daily mix: (none checked — house shuffle uses all folders until you check some)"
                : $"Daily mix: {string.Join(", ", daily.Select(p => System.IO.Path.GetFileName(p.TrimEnd('\\', '/'))))}";
            var detail = roots.Count == 0
                ? message
                : $"{message}\n{string.Join("\n", roots)}\n{dailyLine}";
            SetLibraryFeedback(detail, warn: !ok);
            RefreshLibraryStatusUi();

            // Keep master mapping Sonos combo in sync with newly discovered roots.
            if (ok && roots.Count > 0 && MasterMapSonosCombo is not null
                && string.IsNullOrWhiteSpace(MasterMapSonosCombo.Text)
                && MasterMapSonosCombo.Items.Count > 0)
            {
                MasterMapSonosCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Discover library roots UI failed", ex);
            SetLibraryFeedback(ex.Message, warn: true);
        }
        finally
        {
            if (btn is not null)
            {
                btn.IsEnabled = true;
                btn.Content = prevContent ?? "Discover from Sonos";
            }
        }
    }

    /// <summary>Library tab banner + window status line (Discover/rescan feedback).</summary>
    private void SetLibraryFeedback(string message, bool warn)
    {
        SetStatus(message.Replace('\n', ' '), warn);
        if (LibTabStatusText is not null)
        {
            LibTabStatusText.Text = message;
            LibTabStatusText.Foreground = warn
                ? System.Windows.Media.Brushes.IndianRed
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x46));
        }
    }

    private void StartLibraryRescan(bool forceAll)
    {
        // Persist any manual path edits first (discovery also saves).
        try
        {
            CommitWorkingValuesToSettings();
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("Save before library rescan failed", ex);
            SetStatus($"Could not save settings: {ex.Message}", warn: true);
            return;
        }

        if (_library is null)
        {
            SetStatus("Library service not available.", warn: true);
            return;
        }

        // Empty roots → discover from Sonos inside the scan pipeline.
        var rediscover = _settings.SonosLibraryRoots.Count == 0;
        var (started, message) = _library.RequestRescan(forceAll, rediscoverRoots: rediscover);
        SetStatus(message, warn: !started);
        RefreshLibraryStatusUi();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Re-discover whenever Settings becomes visible from the tray (not only volumes).
        if (_loaded && e.NewValue is true)
            RefreshDevicesInBackground();
    }

    private void LoadWakeUiFromSettings()
    {
        WakeEnabledCheckBox.IsChecked = _settings.WakeEnabled;
        WakeTimeBox.Text = MinutesToHhmm(_settings.WakeMinutes);
        WakeDaySu.IsChecked = _settings.WakeIncludesDay(DayOfWeek.Sunday);
        WakeDayMo.IsChecked = _settings.WakeIncludesDay(DayOfWeek.Monday);
        WakeDayTu.IsChecked = _settings.WakeIncludesDay(DayOfWeek.Tuesday);
        WakeDayWe.IsChecked = _settings.WakeIncludesDay(DayOfWeek.Wednesday);
        WakeDayTh.IsChecked = _settings.WakeIncludesDay(DayOfWeek.Thursday);
        WakeDayFr.IsChecked = _settings.WakeIncludesDay(DayOfWeek.Friday);
        WakeDaySa.IsChecked = _settings.WakeIncludesDay(DayOfWeek.Saturday);
        WakeStartVolumeBox.Text = _settings.WakeStartVolume.ToString();
        WakeEndVolumeBox.Text = _settings.WakeEndVolume.ToString();
        WakeStepBox.Text = _settings.WakeVolumeStep.ToString();
        WakeIntervalBox.Text = _settings.WakeStepIntervalMinutes.ToString();
        WakeExpandCheckBox.IsChecked = _settings.WakeExpandToHouse;

        WakeSourceComboBox.Items.Clear();
        WakeSourceComboBox.Items.Add("Shuffle Music Library");
        WakeSourceComboBox.Items.Add("Favorite / playlist");
        WakeSourceComboBox.SelectedIndex =
            string.Equals(_settings.WakeSource, AppSettings.WakeSourceFavorite, StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
        UpdateWakeFavoriteEnabled();
    }

    private void WakeSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateWakeFavoriteEnabled();

    private void UpdateWakeFavoriteEnabled()
    {
        var fav = WakeSourceComboBox.SelectedIndex == 1;
        WakeFavoriteComboBox.IsEnabled = fav;
    }

    /// <summary>
    /// Full device discovery + favorites + speaker volumes, non-blocking.
    /// Used when Settings opens and for the manual Refresh button.
    /// </summary>
    public void RefreshDevicesInBackground() => _ = RefreshDevicesAsync();

    /// <summary>Legacy name kept for App callers; now runs full discovery.</summary>
    public void RefreshSpeakers() => RefreshDevicesInBackground();

    private async Task RefreshDevicesAsync()
    {
        if (_refreshInProgress)
            return;

        _refreshInProgress = true;
        RefreshButton.IsEnabled = false;
        SetStatus("Discovering Sonos devices…", warn: false);
        try
        {
            var preferred = (RoomComboBox.SelectedItem as SonosGroup)?.CoordinatorRoom
                ?? _settings.ActiveRoom
                ?? _sonos.ActiveRoom;
            await _sonos.RefreshAsync(preferred);
            PopulateRooms();
            await LoadFavoritesAsync();
            await LoadSpeakerVolumesAsync();
            SetStatus($"Found {_sonos.Groups.Count} group(s).", warn: false);
            AppLog.Info($"Settings auto-refresh: {_sonos.Groups.Count} group(s)");
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings refresh discovery failed", ex);
            SetStatus($"Discovery failed: {ex.Message}", warn: true);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _refreshInProgress = false;
        }
    }

    private async Task LoadSpeakerVolumesAsync()
    {
        SpeakersPanel.Children.Clear();

        IReadOnlyList<SpeakerVolume> volumes;
        try
        {
            volumes = await _sonos.GetSpeakerVolumesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Speaker volume list load failed", ex);
            return; // Non-fatal: the user can Refresh once a speaker is reachable.
        }

        foreach (var speaker in volumes)
            SpeakersPanel.Children.Add(BuildSpeakerRow(speaker));
    }

    private UIElement BuildSpeakerRow(SpeakerVolume speaker)
    {
        // Star-sized name/slider so Mute always stays visible when the panel is wide.
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 100 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 120 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = speaker.Reachable ? speaker.RoomName : $"{speaker.RoomName} (offline)",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = speaker.Reachable
                ? System.Windows.Media.Brushes.Black
                : System.Windows.Media.Brushes.Gray,
        };
        Grid.SetColumn(name, 0);

        var valueLabel = new TextBlock
        {
            Text = $"{speaker.Volume}%",
            MinWidth = 36,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = speaker.Volume,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = speaker.Reachable,
            Margin = new Thickness(0, 0, 4, 0),
        };
        slider.ValueChanged += (_, e) => valueLabel.Text = $"{(int)e.NewValue}%";
        slider.PreviewMouseLeftButtonUp += async (_, _) => await CommitSpeakerVolumeAsync(speaker.IpAddress, (int)slider.Value);
        slider.LostKeyboardFocus += async (_, _) => await CommitSpeakerVolumeAsync(speaker.IpAddress, (int)slider.Value);
        Grid.SetColumn(slider, 1);

        Grid.SetColumn(valueLabel, 2);

        var muteCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Mute",
            IsChecked = speaker.Muted,
            IsEnabled = speaker.Reachable,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0),
        };
        muteCheck.Checked += async (_, _) => await _sonos.SetSpeakerMuteAsync(speaker.IpAddress, true);
        muteCheck.Unchecked += async (_, _) => await _sonos.SetSpeakerMuteAsync(speaker.IpAddress, false);
        Grid.SetColumn(muteCheck, 3);

        row.Children.Add(name);
        row.Children.Add(slider);
        row.Children.Add(valueLabel);
        row.Children.Add(muteCheck);
        return row;
    }

    private async Task CommitSpeakerVolumeAsync(string ip, int percent)
    {
        try
        {
            await _sonos.SetSpeakerVolumeAsync(ip, percent);
        }
        catch (Exception ex)
        {
            // Non-fatal; next Refresh will show the true value.
            AppLog.Warn($"Set speaker volume failed ({ip} → {percent}%)", ex);
        }
    }

    private void PopulateRooms()
    {
        _suppressRoomChange = true;
        RoomComboBox.Items.Clear();
        WakeRoomComboBox.Items.Clear();
        foreach (var group in _sonos.Groups)
        {
            RoomComboBox.Items.Add(group);
            WakeRoomComboBox.Items.Add(group);
        }

        var active = _settings.ActiveRoom ?? _sonos.ActiveRoom;
        var match = _sonos.Groups.FirstOrDefault(g =>
            string.Equals(g.CoordinatorRoom, active, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            RoomComboBox.SelectedItem = match;
        else if (RoomComboBox.Items.Count > 0)
            RoomComboBox.SelectedIndex = 0;

        var wakeRoom = _settings.WakeRoom ?? active;
        var wakeMatch = _sonos.Groups.FirstOrDefault(g =>
            string.Equals(g.CoordinatorRoom, wakeRoom, StringComparison.OrdinalIgnoreCase));
        if (wakeMatch is not null)
            WakeRoomComboBox.SelectedItem = wakeMatch;
        else if (WakeRoomComboBox.Items.Count > 0)
            WakeRoomComboBox.SelectedIndex = 0;

        _suppressRoomChange = false;
    }

    private async Task LoadFavoritesAsync()
    {
        IReadOnlyList<string> titles = [];
        try
        {
            var favorites = await _sonos.GetFavoritesAsync();
            titles = favorites.Where(f => f.IsPlayable).Select(f => f.Title).ToList();
        }
        catch (Exception ex)
        {
            // Leave titles empty; the user can Refresh once a room is reachable.
            AppLog.Warn("Favorites load failed", ex);
        }

        _sonosPlayableTitles = titles;

        for (var i = 0; i < _favCombos.Length; i++)
            PopulateSlotCombo(_favCombos[i], titles, _settings.FavoriteSlots[i]);

        PopulateFavoriteCombo(WakeFavoriteComboBox, titles, _settings.WakeFavoriteName);

        RefreshControlPlayList();

        var genreCount = GetPlayGenres().Count;
        if (titles.Count == 0 && _settings.Tags.Count == 0 && genreCount == 0)
            SetStatus("No tags, genres, or Sonos playlists yet. Rescan Library; add tags on Tags tab; Refresh for Sonos favorites.", warn: true);

        RefreshControlShuffleSourceCombo();
    }

    /// <summary>Genres for UI pickers when the option is enabled; empty when disabled.</summary>
    private IReadOnlyList<(string Genre, int Count)> GetPlayGenres()
    {
        if (!_settings.ShowGenresInPlaySources || _library is null)
            return [];
        return _library.ListGenres();
    }

    /// <summary>Slot picker entry: folder, tag, genre, or Sonos favorite/playlist.</summary>
    private sealed record SlotPick(
        string Display,
        string Source,
        string? SonosName,
        string? TagKey,
        string? GenreName = null,
        string? FolderPath = null)
    {
        public override string ToString() => Display;
    }

    private sealed record ControlShufflePick(string Display, string Token)
    {
        public override string ToString() => Display;
    }

    private bool _suppressControlShufflePick;

    private void RefreshControlShuffleSourceCombo()
    {
        if (ControlShuffleSourceCombo is null)
            return;

        _suppressControlShufflePick = true;
        try
        {
            var want = (_settings.ControlShuffleSource ?? AppSettings.ControlShuffleAll).Trim();
            ControlShuffleSourceCombo.Items.Clear();
            ControlShuffleSourceCombo.Items.Add(new ControlShufflePick("All · Daily mix", AppSettings.ControlShuffleAll));

            foreach (var folder in _settings.EnsureShape().SonosLibraryRoots)
            {
                var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(name)) name = folder;
                var count = _library?.CountTracksUnderFolder(folder) ?? 0;
                ControlShuffleSourceCombo.Items.Add(new ControlShufflePick(
                    count > 0 ? $"Folder · {name} ({count})" : $"Folder · {name}",
                    AppSettings.FolderShuffleToken(folder)));
            }

            foreach (var t in _settings.EnsureShape().Tags)
            {
                var count = _library?.GetTracksWithTag(t.Key).Count ?? 0;
                ControlShuffleSourceCombo.Items.Add(new ControlShufflePick(
                    count > 0 ? $"Tag · {t.Label} ({count})" : $"Tag · {t.Label}",
                    $"tag:{t.Key}"));
            }

            foreach (var (genre, count) in GetPlayGenres())
            {
                ControlShuffleSourceCombo.Items.Add(new ControlShufflePick(
                    $"Genre · {genre} ({count})",
                    $"genre:{genre}"));
            }

            object? select = ControlShuffleSourceCombo.Items.OfType<ControlShufflePick>()
                .FirstOrDefault(p => string.Equals(p.Token, want, StringComparison.OrdinalIgnoreCase));

            if (select is null && AppSettings.TryParseFolderShuffleToken(want, out var folderPath))
            {
                var name = System.IO.Path.GetFileName(folderPath.TrimEnd('\\', '/'));
                select = new ControlShufflePick($"Folder · {name}", want);
                ControlShuffleSourceCombo.Items.Add(select);
            }
            else if (select is null && want.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                var key = want["tag:".Length..].Trim();
                var label = _settings.FindTag(key)?.Label ?? key;
                select = new ControlShufflePick($"Tag · {label}", want);
                ControlShuffleSourceCombo.Items.Add(select);
            }
            else if (select is null && want.StartsWith("genre:", StringComparison.OrdinalIgnoreCase))
            {
                var name = want["genre:".Length..].Trim();
                select = new ControlShufflePick($"Genre · {name}", want);
                ControlShuffleSourceCombo.Items.Add(select);
            }

            ControlShuffleSourceCombo.SelectedItem = select ?? ControlShuffleSourceCombo.Items[0];
        }
        finally
        {
            _suppressControlShufflePick = false;
        }
    }

    private void ControlShuffleSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlShufflePick || !_loaded)
            return;
        if (ControlShuffleSourceCombo.SelectedItem is not ControlShufflePick pick)
            return;

        _settings.ControlShuffleSource = pick.Token;
        try { _store.Save(_settings); }
        catch (Exception ex) { AppLog.Warn("Save ControlShuffleSource failed", ex); }
    }

    private void PopulateSlotCombo(ComboBox combo, IReadOnlyList<string> sonosTitles, FavoriteSlot bound)
    {
        combo.Items.Clear();
        combo.Items.Add(new SlotPick(NoneLabel, FavoriteSlot.SourceSonos, null, null));

        foreach (var folder in _settings.EnsureShape().SonosLibraryRoots)
        {
            var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = folder;
            var count = _library?.CountTracksUnderFolder(folder) ?? 0;
            combo.Items.Add(new SlotPick(
                count > 0 ? $"Folder · {name} ({count})" : $"Folder · {name}",
                FavoriteSlot.SourceFolder,
                null,
                null,
                FolderPath: folder));
        }

        foreach (var t in _settings.EnsureShape().Tags)
            combo.Items.Add(new SlotPick($"Tag · {t.Label}", FavoriteSlot.SourceTag, null, t.Key));

        foreach (var (genre, count) in GetPlayGenres())
        {
            combo.Items.Add(new SlotPick(
                $"Genre · {genre} ({count})",
                FavoriteSlot.SourceGenre,
                null,
                null,
                genre));
        }

        foreach (var title in sonosTitles)
            combo.Items.Add(new SlotPick($"Sonos · {title}", FavoriteSlot.SourceSonos, title, null));

        object? select = combo.Items[0];
        if (bound.IsFolder)
        {
            select = combo.Items.OfType<SlotPick>()
                .FirstOrDefault(p => p.Source == FavoriteSlot.SourceFolder
                    && string.Equals(p.FolderPath, bound.FolderPath, StringComparison.OrdinalIgnoreCase))
                ?? select;
            if (select == combo.Items[0] && !string.IsNullOrWhiteSpace(bound.FolderPath))
            {
                var name = System.IO.Path.GetFileName(bound.FolderPath.TrimEnd('\\', '/'));
                var orphan = new SlotPick(
                    $"Folder · {name}",
                    FavoriteSlot.SourceFolder,
                    null,
                    null,
                    FolderPath: bound.FolderPath);
                combo.Items.Insert(1, orphan);
                select = orphan;
            }
        }
        else if (bound.IsTag)
        {
            select = combo.Items.OfType<SlotPick>()
                .FirstOrDefault(p => p.Source == FavoriteSlot.SourceTag
                    && string.Equals(p.TagKey, bound.TagKey, StringComparison.OrdinalIgnoreCase))
                ?? select;
        }
        else if (bound.IsGenre)
        {
            select = combo.Items.OfType<SlotPick>()
                .FirstOrDefault(p => p.Source == FavoriteSlot.SourceGenre
                    && string.Equals(p.GenreName, bound.GenreName, StringComparison.OrdinalIgnoreCase))
                ?? select;
            if (select == combo.Items[0] && !string.IsNullOrWhiteSpace(bound.GenreName))
            {
                var orphan = new SlotPick(
                    $"Genre · {bound.GenreName}",
                    FavoriteSlot.SourceGenre,
                    null,
                    null,
                    bound.GenreName);
                combo.Items.Insert(1, orphan);
                select = orphan;
            }
        }
        else if (bound.IsSonos)
        {
            select = combo.Items.OfType<SlotPick>()
                .FirstOrDefault(p => p.Source == FavoriteSlot.SourceSonos
                    && string.Equals(p.SonosName, bound.FavoriteName, StringComparison.OrdinalIgnoreCase))
                ?? select;
        }

        combo.SelectedItem = select;
    }

    /// <summary>Build Control-tab list: folders, tags, genres, Sonos favorites/playlists.</summary>
    private void RefreshControlPlayList()
    {
        if (ControlPlayListBox is null)
            return;

        var rows = new List<ControlPlayRow>();
        var genres = GetPlayGenres();
        var folders = _settings.EnsureShape().SonosLibraryRoots;

        foreach (var folder in folders)
        {
            var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = folder;
            var count = _library?.CountTracksUnderFolder(folder) ?? 0;
            rows.Add(new ControlPlayRow(
                Kind: ControlPlayKind.Folder,
                KindLabel: "Folder",
                Title: name,
                Detail: count == 0
                    ? "No tracks in cache — Rescan library after Discover"
                    : $"{count} track(s) · folder shuffle · stays in folder for top-up",
                Payload: folder));
        }

        foreach (var t in _settings.EnsureShape().Tags)
        {
            var count = _library?.GetTracksWithTag(t.Key).Count ?? 0;
            rows.Add(new ControlPlayRow(
                Kind: ControlPlayKind.Tag,
                KindLabel: "Tag",
                Title: t.Label,
                Detail: count == 0
                    ? "No tracks tagged yet — tag music in Library / Quick tag"
                    : $"{count} track(s) · shuffled play · library top-up if enabled",
                Payload: t.Key));
        }

        foreach (var (genre, count) in genres)
        {
            rows.Add(new ControlPlayRow(
                Kind: ControlPlayKind.Genre,
                KindLabel: "Genre",
                Title: genre,
                Detail: $"{count} track(s) · shuffled play · library top-up if enabled",
                Payload: genre));
        }

        foreach (var title in _sonosPlayableTitles)
        {
            rows.Add(new ControlPlayRow(
                Kind: ControlPlayKind.Sonos,
                KindLabel: "Sonos",
                Title: title,
                Detail: "Sonos favorite or playlist",
                Payload: title));
        }

        ControlPlayListBox.ItemsSource = rows;
        var genreBit = _settings.ShowGenresInPlaySources
            ? $" · {genres.Count} genre(s)"
            : " · genres hidden";
        ControlPlayListStatus.Text = rows.Count == 0
            ? "No folders, tags, or Sonos playlists yet. Discover library paths; add tags; Refresh for Sonos favorites."
            : $"{folders.Count} folder(s) · {_settings.Tags.Count} tag(s){genreBit} · {_sonosPlayableTitles.Count} Sonos item(s)";
    }

    private async void ControlPlayListRefresh_Click(object sender, RoutedEventArgs e)
    {
        var btn = ControlPlayListRefreshButton ?? sender as System.Windows.Controls.Button;
        var prev = btn?.Content;
        if (btn is not null)
        {
            btn.IsEnabled = false;
            btn.Content = "Refreshing…";
        }

        ControlPlayListStatus.Text = "Refreshing folders, tags, genres & Sonos playlists…";
        ControlPlayListStatus.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x46));
        SetStatus("Refreshing play list…", warn: false);

        try
        {
            // Local lists first (instant), then Sonos favorites (network).
            RefreshControlShuffleSourceCombo();
            RefreshControlPlayList();
            ControlPlayListStatus.Text = "Loading Sonos favorites/playlists…";

            await LoadFavoritesAsync().ConfigureAwait(true);

            var folders = _settings.EnsureShape().SonosLibraryRoots.Count;
            var tags = _settings.Tags.Count;
            var genres = GetPlayGenres().Count;
            var sonos = _sonosPlayableTitles.Count;
            var summary =
                $"Refreshed · {folders} folder(s) · {tags} tag(s) · {genres} genre(s) · {sonos} Sonos item(s)";
            ControlPlayListStatus.Text = summary;
            ControlPlayListStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x46));
            SetStatus(summary, warn: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Control play list refresh failed", ex);
            ControlPlayListStatus.Text = $"Refresh failed: {ex.Message}";
            ControlPlayListStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
            SetStatus(ex.Message, warn: true);
        }
        finally
        {
            if (btn is not null)
            {
                btn.IsEnabled = true;
                btn.Content = prev ?? "Refresh list";
            }
        }
    }

    private void ControlPlayItem_Click(object sender, RoutedEventArgs e)
    {
        if (_controlPlayBusy)
            return;
        if (sender is not FrameworkElement { Tag: ControlPlayRow row })
            return;

        _controlPlayBusy = true;
        ControlPlayListStatus.Text = $"Starting “{row.Title}”…";
        SetStatus($"Playing “{row.Title}”…", warn: false);

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                string toast;
                if (row.Kind is ControlPlayKind.Folder or ControlPlayKind.Tag or ControlPlayKind.Genre)
                {
                    if (_library is null)
                    {
                        SetStatus("Library service not available.", warn: true);
                        ControlPlayListStatus.Text = "Library not available.";
                        return;
                    }

                    toast = row.Kind switch
                    {
                        ControlPlayKind.Folder => await _sonos.PlayLibraryFolderAsync(_library, row.Payload, shuffle: true),
                        ControlPlayKind.Tag => await _sonos.PlayTaggedTracksAsync(_library, row.Payload, shuffle: true),
                        _ => await _sonos.PlayGenreTracksAsync(_library, row.Payload, shuffle: true),
                    };
                }
                else
                {
                    toast = await _sonos.PlaySonosFavoriteByNameAsync(row.Payload);
                }

                ControlPlayListStatus.Text = toast;
                SetStatus(toast, warn: false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Control play failed", ex);
                ControlPlayListStatus.Text = ex.Message;
                SetStatus(ex.Message, warn: true);
            }
            finally
            {
                _controlPlayBusy = false;
            }
        }), DispatcherPriority.Background);
    }

    private enum ControlPlayKind
    {
        Folder,
        Tag,
        Genre,
        Sonos,
    }

    private sealed record ControlPlayRow(
        ControlPlayKind Kind,
        string KindLabel,
        string Title,
        string Detail,
        string Payload);

    private static void PopulateFavoriteCombo(ComboBox combo, IReadOnlyList<string> titles, string? selected)
    {
        combo.Items.Clear();
        combo.Items.Add(NoneLabel);
        foreach (var title in titles)
            combo.Items.Add(title);

        combo.SelectedItem = !string.IsNullOrWhiteSpace(selected) && titles.Contains(selected)
            ? selected
            : NoneLabel;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshDevicesInBackground();

    private async void RoomComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRoomChange || RoomComboBox.SelectedItem is not SonosGroup group)
            return;

        _sonos.SetActiveRoom(group.CoordinatorRoom);
        _onRoomChanged(group.CoordinatorRoom);
        await LoadFavoritesAsync();
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            box.SelectAll();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not TextBox box || !_boxToConfig.TryGetValue(box, out var cfg))
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            ResetConfig(cfg);
            box.Text = "";
            return;
        }

        if (IsModifierKey(key))
            return; // wait for a non-modifier key

        cfg.Control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        cfg.Alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        cfg.Shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        cfg.Win = (Keyboard.Modifiers & ModifierKeys.Windows) != 0;
        cfg.Key = key.ToString();
        box.Text = cfg.ToString();
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _byTag.TryGetValue(tag, out var entry))
        {
            ResetConfig(entry.Config);
            entry.Box.Text = "";
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitAndPersist(out var error, out var failures))
        {
            SetStatus(error!, warn: true);
            return;
        }

        if (failures.Count > 0)
        {
            SetStatus($"Saved, but these hotkeys are in use elsewhere: {string.Join(", ", failures)}", warn: true);
        }
        else
        {
            SetStatus("Saved. Hotkeys are active.", warn: false);
        }

        // Genre visibility / shuffle picker may have changed.
        RefreshControlShuffleSourceCombo();
        RefreshControlPlayList();
        _ = LoadFavoritesAsync();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        // Apply working hotkeys/checkboxes the same way as Save so Hide does not drop edits.
        if (!CommitAndPersist(out var error, out _))
            SetStatus(error!, warn: true);

        HideToTrayRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Copies UI working state into <see cref="_settings"/>, writes JSON, and re-registers hotkeys.
    /// </summary>
    private bool CommitAndPersist(out string? error, out IReadOnlyList<HotsonosAction> failures)
    {
        error = null;
        failures = [];
        CaptureWindowGeometry();
        CommitWorkingValuesToSettings();

        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            error = $"Could not save settings: {ex.Message}";
            return false;
        }

        failures = _applyBindings();
        return true;
    }

    /// <summary>Copies hotkey boxes, checkboxes, and combos into the live settings object.</summary>
    private void CommitWorkingValuesToSettings()
    {
        _settings.LevelVolumes = _levelVolumes;
        _settings.FreshStart = _freshStart;
        _settings.ShuffleLibrary = _shuffle;
        _settings.PlayPause = _playPause;
        _settings.Next = _next;
        _settings.Previous = _previous;
        _settings.VolumeUp = _volumeUp;
        _settings.VolumeDown = _volumeDown;
        _settings.Mute = _mute;
        _settings.QuickTag = _quickTag;
        _settings.QuickPlay = _quickPlay;
        if (int.TryParse(VolumeStepBox.Text, out var step) && step is >= 1 and <= 50)
            _settings.VolumeStep = step;
        if (int.TryParse(LevelPercentBox.Text, out var level) && level is >= 0 and <= 100)
            _settings.LevelVolumePercent = level;
        _settings.ShowFlyoutOnTrackChange = FlyoutOnTrackChangeCheckBox.IsChecked == true;
        _settings.ShowFlyoutOnAction = FlyoutOnActionCheckBox.IsChecked == true;
        if (TrayDoubleClickCombo.SelectedItem is TrayDoubleClickPick pick)
            _settings.TrayDoubleClickAction = pick.Value;
        _settings.NightlyResetEnabled = NightlyResetCheckBox.IsChecked == true;
        if (TryParseHhmm(NightlyResetTimeBox.Text, out var minutes))
            _settings.NightlyResetMinutes = minutes;
        _settings.NightlyResetReshuffle = NightlyResetReshuffleCheckBox.IsChecked == true;
        if (RoomComboBox.SelectedItem is SonosGroup group)
            _settings.ActiveRoom = group.CoordinatorRoom;

        for (var i = 0; i < _favCombos.Length; i++)
        {
            _settings.FavoriteSlots[i].Hotkey = _favHotkeys[i];
            if (_favCombos[i].SelectedItem is SlotPick slotPick
                && !string.Equals(slotPick.Display, NoneLabel, StringComparison.Ordinal))
            {
                if (slotPick.Source == FavoriteSlot.SourceFolder && !string.IsNullOrWhiteSpace(slotPick.FolderPath))
                {
                    _settings.FavoriteSlots[i].Source = FavoriteSlot.SourceFolder;
                    _settings.FavoriteSlots[i].FolderPath = slotPick.FolderPath;
                    _settings.FavoriteSlots[i].TagKey = null;
                    _settings.FavoriteSlots[i].GenreName = null;
                    _settings.FavoriteSlots[i].FavoriteName = null;
                }
                else if (slotPick.Source == FavoriteSlot.SourceTag && !string.IsNullOrWhiteSpace(slotPick.TagKey))
                {
                    _settings.FavoriteSlots[i].Source = FavoriteSlot.SourceTag;
                    _settings.FavoriteSlots[i].TagKey = slotPick.TagKey;
                    _settings.FavoriteSlots[i].FavoriteName = null;
                    _settings.FavoriteSlots[i].GenreName = null;
                    _settings.FavoriteSlots[i].FolderPath = null;
                }
                else if (slotPick.Source == FavoriteSlot.SourceGenre && !string.IsNullOrWhiteSpace(slotPick.GenreName))
                {
                    _settings.FavoriteSlots[i].Source = FavoriteSlot.SourceGenre;
                    _settings.FavoriteSlots[i].GenreName = slotPick.GenreName;
                    _settings.FavoriteSlots[i].FavoriteName = null;
                    _settings.FavoriteSlots[i].TagKey = null;
                    _settings.FavoriteSlots[i].FolderPath = null;
                }
                else if (!string.IsNullOrWhiteSpace(slotPick.SonosName))
                {
                    _settings.FavoriteSlots[i].Source = FavoriteSlot.SourceSonos;
                    _settings.FavoriteSlots[i].FavoriteName = slotPick.SonosName;
                    _settings.FavoriteSlots[i].TagKey = null;
                    _settings.FavoriteSlots[i].GenreName = null;
                    _settings.FavoriteSlots[i].FolderPath = null;
                }
                else
                {
                    _settings.FavoriteSlots[i].Source = FavoriteSlot.SourceSonos;
                    _settings.FavoriteSlots[i].FavoriteName = null;
                    _settings.FavoriteSlots[i].TagKey = null;
                    _settings.FavoriteSlots[i].GenreName = null;
                    _settings.FavoriteSlots[i].FolderPath = null;
                }
            }
            else
            {
                _settings.FavoriteSlots[i].Source = FavoriteSlot.SourceSonos;
                _settings.FavoriteSlots[i].FavoriteName = null;
                _settings.FavoriteSlots[i].TagKey = null;
                _settings.FavoriteSlots[i].GenreName = null;
                _settings.FavoriteSlots[i].FolderPath = null;
            }
        }

        _settings.McpEnabled = McpEnabledCheckBox.IsChecked == true;
        if (int.TryParse(McpPortBox.Text, out var mcpPort) && mcpPort is >= 1024 and <= 65535)
            _settings.McpPort = mcpPort;

        if (int.TryParse(ShuffleQueueTracksBox.Text, out var qn) && qn is >= 20 and <= 500)
            _settings.ShuffleQueueTracks = qn;
        if (int.TryParse(ShuffleTopUpTracksBox.Text, out var tu) && tu is >= 10 and <= 300)
            _settings.ShuffleTopUpTracks = tu;
        if (int.TryParse(ShuffleHistoryDaysBox.Text, out var hd) && hd is >= 1 and <= 90)
            _settings.ShuffleHistoryDays = hd;
        if (int.TryParse(ShuffleTopUpRemainingBox.Text, out var rem) && rem is >= 1 and <= 30)
            _settings.ShuffleTopUpWhenRemaining = rem;
        _settings.ShuffleExcludePlayed = ShuffleExcludePlayedCheckBox.IsChecked == true;
        _settings.ShuffleAutoTopUp = ShuffleAutoTopUpCheckBox.IsChecked == true;
        _settings.ContinueLibraryShuffleAfterSpecialPlay = ContinueShuffleAfterSpecialPlayCheckBox.IsChecked == true;
        _settings.ShowGenresInPlaySources = ShowGenresInPlaySourcesCheckBox.IsChecked == true;
        if (ControlShuffleSourceCombo.SelectedItem is ControlShufflePick shufflePick)
            _settings.ControlShuffleSource = shufflePick.Token;
        _settings.ShuffleArtistSpread = ShuffleArtistSpreadCheckBox.IsChecked == true;

        _settings.SonosLibraryRoots = SnapshotLibraryRoots();
        _settings.DailyLibraryRoots = SnapshotDailyLibraryRoots();
        _settings.MasterLibraryMappings = SnapshotMasterMappings();
        // Legacy single field kept in sync by EnsureShape from first mapping.
        _settings.MasterLibraryRoot = _settings.MasterLibraryMappings.FirstOrDefault()?.MasterRoot;

        CommitWakeUiToSettings();
    }

    private void LoadMasterMappingsUi(IEnumerable<MasterLibraryMapping>? mappings)
    {
        _masterMappings.Clear();
        if (mappings is null)
            return;
        foreach (var m in mappings)
        {
            if (string.IsNullOrWhiteSpace(m.SonosPath) || string.IsNullOrWhiteSpace(m.MasterRoot))
                continue;
            _masterMappings.Add(new MasterLibraryMapping
            {
                SonosPath = m.SonosPath.Trim().TrimEnd('\\', '/'),
                MasterRoot = m.MasterRoot.Trim().TrimEnd('\\', '/'),
            });
        }
    }

    private List<MasterLibraryMapping> SnapshotMasterMappings() =>
        _masterMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.SonosPath) && !string.IsNullOrWhiteSpace(m.MasterRoot))
            .Select(m => new MasterLibraryMapping
            {
                SonosPath = m.SonosPath.Trim().TrimEnd('\\', '/'),
                MasterRoot = m.MasterRoot.Trim().TrimEnd('\\', '/'),
            })
            .GroupBy(m => m.SonosPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

    private List<string> SnapshotLibraryRoots() =>
        _libraryFolders
            .Where(f => !string.IsNullOrWhiteSpace(f.Path))
            .Select(f => f.Path.Trim().TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<string> SnapshotDailyLibraryRoots() =>
        _libraryFolders
            .Where(f => f.InDaily && !string.IsNullOrWhiteSpace(f.Path))
            .Select(f => f.Path.Trim().TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void RebuildLibraryFoldersUi(bool preserveDailyChecks)
    {
        HashSet<string> dailyChecked;
        if (preserveDailyChecks)
        {
            dailyChecked = _libraryFolders
                .Where(f => f.InDaily)
                .Select(f => f.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var s = _settings.EnsureShape();
            dailyChecked = s.DailyLibraryRoots.Count > 0
                ? s.DailyLibraryRoots.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : s.GetEffectiveDailyLibraryRoots().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var roots = _settings.EnsureShape().SonosLibraryRoots.ToList();
        if (roots.Count == 0 && _libraryFolders.Count > 0 && preserveDailyChecks)
            roots = SnapshotLibraryRoots();

        _libraryFolders.Clear();
        foreach (var r in roots.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var path = r.Trim().TrimEnd('\\', '/');
            var inDaily = dailyChecked.Count == 0
                ? roots.Count <= 1
                : dailyChecked.Contains(path)
                  || dailyChecked.Any(c =>
                      path.StartsWith(c.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                      || c.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            _libraryFolders.Add(new LibraryFolderRow
            {
                Path = path,
                InDaily = inDaily,
            });
        }
    }

    private void RefreshMasterMapSonosCombo()
    {
        if (MasterMapSonosCombo is null)
            return;

        var selectedPath = (MasterMapSonosCombo.SelectedItem as LibraryFolderRow)?.Path;
        MasterMapSonosCombo.ItemsSource = null;
        MasterMapSonosCombo.ItemsSource = _libraryFolders.ToList();

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            var match = _libraryFolders.FirstOrDefault(f =>
                string.Equals(f.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                MasterMapSonosCombo.SelectedItem = match;
        }
        else if (MasterMapSonosCombo.Items.Count > 0 && MasterMapSonosCombo.SelectedIndex < 0)
        {
            MasterMapSonosCombo.SelectedIndex = 0;
        }
    }

    private void LibraryFolderAdd_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a music library folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        var path = dlg.SelectedPath.Trim().TrimEnd('\\', '/');
        if (_libraryFolders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That folder is already in the list.",
                "Library folders", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _libraryFolders.Add(new LibraryFolderRow
        {
            Path = path,
            InDaily = _libraryFolders.Count == 0, // first folder → daily by default
        });
        // Keep settings roots in sync for other code paths during this session
        _settings.SonosLibraryRoots = SnapshotLibraryRoots();
        RefreshMasterMapSonosCombo();
    }

    private void LibraryFolderRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = LibraryFoldersList.SelectedItems.OfType<LibraryFolderRow>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select one or more folders in the list to remove.",
                "Library folders", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var row in selected)
            _libraryFolders.Remove(row);
        _settings.SonosLibraryRoots = SnapshotLibraryRoots();
        RefreshMasterMapSonosCombo();
    }

    private void LibraryFolderShuffle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LibraryFolderRow row })
            return;
        if (string.IsNullOrWhiteSpace(row.Path))
            return;
        if (_library is null)
        {
            SetLibraryFeedback("Library service not available.", warn: true);
            return;
        }

        // Persist Daily checks / roots before long ops (same as other library actions).
        try
        {
            _settings.SonosLibraryRoots = SnapshotLibraryRoots();
            _settings.DailyLibraryRoots = SnapshotDailyLibraryRoots();
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Save before folder shuffle failed", ex);
        }

        var name = row.DisplayName;
        SetLibraryFeedback($"Shuffling folder “{name}”…", warn: false);
        if (sender is System.Windows.Controls.Button btn)
            btn.IsEnabled = false;

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await _sonos.GroupAllSpeakersAsync().ConfigureAwait(true);
                var toast = await _sonos.PlayLibraryFolderAsync(_library, row.Path, shuffle: true)
                    .ConfigureAwait(true);
                SetLibraryFeedback(toast, warn: false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Library folder shuffle failed", ex);
                SetLibraryFeedback(ex.Message, warn: true);
            }
            finally
            {
                if (sender is System.Windows.Controls.Button b)
                    b.IsEnabled = true;
            }
        }), DispatcherPriority.Background);
    }

    private void MasterMappingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MasterMappingsList.SelectedItem is not MasterLibraryMapping m)
            return;
        var row = _libraryFolders.FirstOrDefault(f =>
            string.Equals(f.Path, m.SonosPath, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
            MasterMapSonosCombo.SelectedItem = row;
        else
        {
            // Mapping points at a path not in the folder list — show path in readonly master box only
            MasterMapSonosCombo.SelectedItem = null;
        }

        MasterMapMasterBox.Text = m.MasterRoot;
    }

    private void MasterMapBrowseMaster_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select master (hi-res) library folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        var start = (MasterMapMasterBox.Text ?? "").Trim();
        if (start.Length > 0)
        {
            try { dlg.SelectedPath = start; }
            catch { /* ignore invalid start path */ }
        }

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;
        MasterMapMasterBox.Text = dlg.SelectedPath;
    }

    private void MasterMapAddUpdate_Click(object sender, RoutedEventArgs e)
    {
        var folder = MasterMapSonosCombo.SelectedItem as LibraryFolderRow;
        var sonos = folder?.Path?.Trim().TrimEnd('\\', '/') ?? "";
        var master = (MasterMapMasterBox.Text ?? "").Trim().TrimEnd('\\', '/');
        if (sonos.Length == 0)
        {
            MessageBox.Show(this, "Choose a library folder from the list (Discover or Add folder first).",
                "Master mapping", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (master.Length == 0)
        {
            MessageBox.Show(this, "Browse to the master (hi-res) folder.",
                "Master mapping", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existing = _masterMappings.FirstOrDefault(m =>
            string.Equals(m.SonosPath, sonos, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var idx = _masterMappings.IndexOf(existing);
            _masterMappings.RemoveAt(idx);
            _masterMappings.Insert(idx, new MasterLibraryMapping { SonosPath = sonos, MasterRoot = master });
            MasterMappingsList.SelectedIndex = idx;
        }
        else
        {
            _masterMappings.Add(new MasterLibraryMapping { SonosPath = sonos, MasterRoot = master });
            MasterMappingsList.SelectedIndex = _masterMappings.Count - 1;
        }
    }

    private void MasterMapRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MasterMappingsList.SelectedItem is MasterLibraryMapping m)
        {
            _masterMappings.Remove(m);
            return;
        }

        MessageBox.Show(this, "Select a mapping in the list to remove.",
            "Master mapping", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed class LibraryFolderRow : INotifyPropertyChanged
    {
        private bool _inDaily;
        public string Path { get; set; } = "";
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Path)
                ? ""
                : System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));

        public bool InDaily
        {
            get => _inDaily;
            set
            {
                if (_inDaily == value) return;
                _inDaily = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InDaily)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void CommitWakeUiToSettings()
    {
        _settings.WakeEnabled = WakeEnabledCheckBox.IsChecked == true;
        if (TryParseHhmm(WakeTimeBox.Text, out var wakeMinutes))
            _settings.WakeMinutes = wakeMinutes;
        _settings.SetWakeDay(DayOfWeek.Sunday, WakeDaySu.IsChecked == true);
        _settings.SetWakeDay(DayOfWeek.Monday, WakeDayMo.IsChecked == true);
        _settings.SetWakeDay(DayOfWeek.Tuesday, WakeDayTu.IsChecked == true);
        _settings.SetWakeDay(DayOfWeek.Wednesday, WakeDayWe.IsChecked == true);
        _settings.SetWakeDay(DayOfWeek.Thursday, WakeDayTh.IsChecked == true);
        _settings.SetWakeDay(DayOfWeek.Friday, WakeDayFr.IsChecked == true);
        _settings.SetWakeDay(DayOfWeek.Saturday, WakeDaySa.IsChecked == true);
        if (WakeRoomComboBox.SelectedItem is SonosGroup wakeRoom)
            _settings.WakeRoom = wakeRoom.CoordinatorRoom;
        _settings.WakeSource = WakeSourceComboBox.SelectedIndex == 1
            ? AppSettings.WakeSourceFavorite
            : AppSettings.WakeSourceShuffle;
        var wakeFav = WakeFavoriteComboBox.SelectedItem as string;
        _settings.WakeFavoriteName =
            string.Equals(wakeFav, NoneLabel, StringComparison.Ordinal) ? null : wakeFav;
        if (int.TryParse(WakeStartVolumeBox.Text, out var start) && start is >= 0 and <= 100)
            _settings.WakeStartVolume = start;
        if (int.TryParse(WakeEndVolumeBox.Text, out var end) && end is >= 0 and <= 100)
            _settings.WakeEndVolume = end;
        if (int.TryParse(WakeStepBox.Text, out var wstep) && wstep is >= 1 and <= 100)
            _settings.WakeVolumeStep = wstep;
        if (int.TryParse(WakeIntervalBox.Text, out var interval) && interval is >= 1 and <= 120)
            _settings.WakeStepIntervalMinutes = interval;
        _settings.WakeExpandToHouse = WakeExpandCheckBox.IsChecked == true;
    }

    private sealed record TrayDoubleClickPick(string Display, string Value)
    {
        public override string ToString() => Display;
    }

    private void LoadTrayDoubleClickCombo()
    {
        TrayDoubleClickCombo.Items.Clear();
        TrayDoubleClickCombo.Items.Add(new TrayDoubleClickPick("Shuffle Music Library", AppSettings.TrayDoubleClickShuffle));
        TrayDoubleClickCombo.Items.Add(new TrayDoubleClickPick("Open Control", AppSettings.TrayDoubleClickControl));
        TrayDoubleClickCombo.Items.Add(new TrayDoubleClickPick("Open Library", AppSettings.TrayDoubleClickLibrary));
        var want = _settings.EnsureShape().TrayDoubleClickAction;
        TrayDoubleClickCombo.SelectedItem = TrayDoubleClickCombo.Items.OfType<TrayDoubleClickPick>()
            .FirstOrDefault(p => string.Equals(p.Value, want, StringComparison.OrdinalIgnoreCase))
            ?? TrayDoubleClickCombo.Items[0];
    }

    private void ShuffleLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        // Persist picker selection immediately.
        if (ControlShuffleSourceCombo.SelectedItem is ControlShufflePick pick)
            _settings.ControlShuffleSource = pick.Token;

        var token = (_settings.ControlShuffleSource ?? AppSettings.ControlShuffleAll).Trim();
        if (string.IsNullOrEmpty(token) ||
            string.Equals(token, AppSettings.ControlShuffleAll, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Starting full library shuffle…", warn: false);
            _runAction(HotsonosAction.ShuffleLibrary);
            return;
        }

        if (AppSettings.TryParseFolderShuffleToken(token, out var folderPath))
        {
            if (_library is null)
            {
                SetStatus("Library service not available.", warn: true);
                return;
            }

            var name = System.IO.Path.GetFileName(folderPath.TrimEnd('\\', '/'));
            SetStatus($"Shuffling folder “{name}”…", warn: false);
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await _sonos.GroupAllSpeakersAsync();
                    var toast = await _sonos.PlayLibraryFolderAsync(_library, folderPath, shuffle: true);
                    SetStatus(toast, warn: false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Control shuffle folder failed", ex);
                    SetStatus(ex.Message, warn: true);
                }
            }), DispatcherPriority.Background);
            return;
        }

        if (token.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var key = token["tag:".Length..].Trim();
            if (key.Length == 0)
            {
                SetStatus("Pick a tag in the shuffle From list.", warn: true);
                return;
            }

            if (_library is null)
            {
                SetStatus("Library service not available.", warn: true);
                return;
            }

            var label = _settings.FindTag(key)?.Label ?? key;
            SetStatus($"Shuffling tag “{label}”…", warn: false);
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var toast = await _sonos.PlayTaggedTracksAsync(_library, key, shuffle: true);
                    SetStatus(toast, warn: false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Control shuffle tag failed", ex);
                    SetStatus(ex.Message, warn: true);
                }
            }), DispatcherPriority.Background);
            return;
        }

        if (token.StartsWith("genre:", StringComparison.OrdinalIgnoreCase))
        {
            var genre = token["genre:".Length..].Trim();
            if (genre.Length == 0)
            {
                SetStatus("Pick a genre in the shuffle From list.", warn: true);
                return;
            }

            if (_library is null)
            {
                SetStatus("Library service not available.", warn: true);
                return;
            }

            SetStatus($"Shuffling genre “{genre}”…", warn: false);
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var toast = await _sonos.PlayGenreTracksAsync(_library, genre, shuffle: true);
                    SetStatus(toast, warn: false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Control shuffle genre failed", ex);
                    SetStatus(ex.Message, warn: true);
                }
            }), DispatcherPriority.Background);
            return;
        }

        SetStatus("Starting full library shuffle…", warn: false);
        _runAction(HotsonosAction.ShuffleLibrary);
    }

    private void FreshStartButton_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Re-syncing all speakers and starting a fresh shuffle…", warn: false);
        _runAction(HotsonosAction.FreshStart);
    }

    private void LevelVolumesButton_Click(object sender, RoutedEventArgs e)
    {
        var pct = int.TryParse(LevelPercentBox.Text, out var p) && p is >= 0 and <= 100 ? p : 20;
        SetStatus($"Setting all speakers to {pct}%…", warn: false);
        if (_settings.LevelVolumePercent != pct)
        {
            _settings.LevelVolumePercent = pct; // honor the field value even if not yet Saved
            TrySaveLevelPercent();
        }
        _runAction(HotsonosAction.LevelVolumes);
    }

    private void TrySaveLevelPercent()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("Level-percent save failed", ex);
        }
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStartupPreference)
            return;

        try
        {
            WindowsStartupManager.SetEnabled(StartWithWindowsCheckBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            _loadingStartupPreference = true;
            StartWithWindowsCheckBox.IsChecked = WindowsStartupManager.IsEnabled();
            _loadingStartupPreference = false;
            MessageBox.Show(this, $"Unable to update the Windows startup setting.\n\n{ex.Message}",
                "HotSonos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadStartupPreference()
    {
        _loadingStartupPreference = true;
        StartWithWindowsCheckBox.IsChecked = WindowsStartupManager.IsEnabled();
        _loadingStartupPreference = false;
    }

    private void SetStatus(string message, bool warn)
    {
        StatusText.Text = message;
        StatusText.Foreground = warn
            ? System.Windows.Media.Brushes.IndianRed
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x46));
    }

    private static string MinutesToHhmm(int minutes) =>
        $"{minutes / 60:D2}:{minutes % 60:D2}";

    private static bool TryParseHhmm(string? text, out int minutes)
    {
        minutes = 0;
        var parts = (text ?? "").Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            && h is >= 0 and <= 23 && m is >= 0 and <= 59)
        {
            minutes = h * 60 + m;
            return true;
        }
        return false;
    }

    private static HotkeyConfig Clone(HotkeyConfig c) => new()
    {
        Control = c.Control,
        Alt = c.Alt,
        Shift = c.Shift,
        Win = c.Win,
        Key = c.Key,
    };

    private static void ResetConfig(HotkeyConfig c)
    {
        c.Control = c.Alt = c.Shift = c.Win = false;
        c.Key = "";
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System;
}
