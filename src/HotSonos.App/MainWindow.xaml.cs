using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HotSonos.App.Infrastructure;
using HotSonos.App.Models;
using HotSonos.App.Services;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace HotSonos.App;

public partial class MainWindow : Window
{
    private const string NoneLabel = "(none)";

    private readonly SonosManager _sonos;
    private readonly ConfigStore _store;
    private readonly AppSettings _settings;
    private readonly Func<IReadOnlyList<HotsonosAction>> _applyBindings;
    private readonly Action<string> _onRoomChanged;
    private readonly Action<HotsonosAction> _runAction;

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
    private readonly HotkeyConfig[] _favHotkeys;

    private readonly Dictionary<TextBox, HotkeyConfig> _boxToConfig = [];
    private readonly Dictionary<string, (TextBox Box, HotkeyConfig Config)> _byTag = [];
    private ComboBox[] _favCombos = [];
    private bool _loadingStartupPreference;
    private bool _suppressRoomChange;

    public event EventHandler? HideToTrayRequested;

    public MainWindow(
        SonosManager sonos,
        ConfigStore store,
        AppSettings settings,
        Func<IReadOnlyList<HotsonosAction>> applyBindings,
        Action<string> onRoomChanged,
        Action<HotsonosAction> runAction)
    {
        _sonos = sonos;
        _store = store;
        _settings = settings.EnsureShape();
        _applyBindings = applyBindings;
        _onRoomChanged = onRoomChanged;
        _runAction = runAction;

        _levelVolumes = Clone(_settings.LevelVolumes);
        _freshStart = Clone(_settings.FreshStart);
        _shuffle = Clone(_settings.ShuffleLibrary);
        _playPause = Clone(_settings.PlayPause);
        _next = Clone(_settings.Next);
        _previous = Clone(_settings.Previous);
        _volumeUp = Clone(_settings.VolumeUp);
        _volumeDown = Clone(_settings.VolumeDown);
        _mute = Clone(_settings.Mute);
        _favHotkeys = _settings.FavoriteSlots.Select(s => Clone(s.Hotkey)).ToArray();

        InitializeComponent();
        Title = $"{AppVersion.DisplayName} Settings";
        RestoreWindowGeometry();
        Loaded += OnLoaded;
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
        CaptureWindowGeometry();
        try { _store.Save(_settings); } catch { /* non-fatal */ }
        base.OnClosing(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _favCombos = [Fav1NameCombo, Fav2NameCombo, Fav3NameCombo, Fav4NameCombo];

        _boxToConfig[LevelVolumesHotkeyBox] = _levelVolumes;
        _boxToConfig[FreshStartHotkeyBox] = _freshStart;
        _boxToConfig[ShuffleHotkeyBox] = _shuffle;
        _boxToConfig[PlayPauseHotkeyBox] = _playPause;
        _boxToConfig[NextHotkeyBox] = _next;
        _boxToConfig[PreviousHotkeyBox] = _previous;
        _boxToConfig[VolumeUpHotkeyBox] = _volumeUp;
        _boxToConfig[VolumeDownHotkeyBox] = _volumeDown;
        _boxToConfig[MuteHotkeyBox] = _mute;
        _boxToConfig[Fav1HotkeyBox] = _favHotkeys[0];
        _boxToConfig[Fav2HotkeyBox] = _favHotkeys[1];
        _boxToConfig[Fav3HotkeyBox] = _favHotkeys[2];
        _boxToConfig[Fav4HotkeyBox] = _favHotkeys[3];

        _byTag["LevelVolumes"] = (LevelVolumesHotkeyBox, _levelVolumes);
        _byTag["FreshStart"] = (FreshStartHotkeyBox, _freshStart);
        _byTag["Shuffle"] = (ShuffleHotkeyBox, _shuffle);
        _byTag["PlayPause"] = (PlayPauseHotkeyBox, _playPause);
        _byTag["Next"] = (NextHotkeyBox, _next);
        _byTag["Previous"] = (PreviousHotkeyBox, _previous);
        _byTag["VolumeUp"] = (VolumeUpHotkeyBox, _volumeUp);
        _byTag["VolumeDown"] = (VolumeDownHotkeyBox, _volumeDown);
        _byTag["Mute"] = (MuteHotkeyBox, _mute);
        _byTag["Fav1"] = (Fav1HotkeyBox, _favHotkeys[0]);
        _byTag["Fav2"] = (Fav2HotkeyBox, _favHotkeys[1]);
        _byTag["Fav3"] = (Fav3HotkeyBox, _favHotkeys[2]);
        _byTag["Fav4"] = (Fav4HotkeyBox, _favHotkeys[3]);

        foreach (var (box, cfg) in _boxToConfig)
            box.Text = cfg.ToString();

        FlyoutOnTrackChangeCheckBox.IsChecked = _settings.ShowFlyoutOnTrackChange;
        FlyoutOnActionCheckBox.IsChecked = _settings.ShowFlyoutOnAction;
        VolumeStepBox.Text = _settings.VolumeStep.ToString();
        LevelPercentBox.Text = _settings.LevelVolumePercent.ToString();
        NightlyResetCheckBox.IsChecked = _settings.NightlyResetEnabled;
        NightlyResetTimeBox.Text = MinutesToHhmm(_settings.NightlyResetMinutes);
        LoadStartupPreference();

        PopulateRooms();
        _ = LoadFavoritesAsync();
        _ = LoadSpeakerVolumesAsync();
    }

    /// <summary>Re-reads every speaker's volume/mute; called each time the window is shown from the tray.</summary>
    public void RefreshSpeakers() => _ = LoadSpeakerVolumesAsync();

    private async Task LoadSpeakerVolumesAsync()
    {
        SpeakersPanel.Children.Clear();

        IReadOnlyList<SpeakerVolume> volumes;
        try
        {
            volumes = await _sonos.GetSpeakerVolumesAsync();
        }
        catch
        {
            return; // Non-fatal: the user can Refresh once a speaker is reachable.
        }

        foreach (var speaker in volumes)
            SpeakersPanel.Children.Add(BuildSpeakerRow(speaker));
    }

    private UIElement BuildSpeakerRow(SpeakerVolume speaker)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

        row.Children.Add(new TextBlock
        {
            Text = speaker.Reachable ? speaker.RoomName : $"{speaker.RoomName} (offline)",
            Width = 90,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = speaker.Reachable
                ? System.Windows.Media.Brushes.Black
                : System.Windows.Media.Brushes.Gray,
        });

        var valueLabel = new TextBlock
        {
            Text = $"{speaker.Volume}%",
            Width = 32,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = speaker.Volume,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = speaker.Reachable,
        };
        slider.ValueChanged += (_, e) => valueLabel.Text = $"{(int)e.NewValue}%";
        slider.PreviewMouseLeftButtonUp += async (_, _) => await CommitSpeakerVolumeAsync(speaker.IpAddress, (int)slider.Value);
        slider.LostKeyboardFocus += async (_, _) => await CommitSpeakerVolumeAsync(speaker.IpAddress, (int)slider.Value);

        var muteCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Mute",
            IsChecked = speaker.Muted,
            IsEnabled = speaker.Reachable,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        muteCheck.Checked += async (_, _) => await _sonos.SetSpeakerMuteAsync(speaker.IpAddress, true);
        muteCheck.Unchecked += async (_, _) => await _sonos.SetSpeakerMuteAsync(speaker.IpAddress, false);

        row.Children.Add(slider);
        row.Children.Add(valueLabel);
        row.Children.Add(muteCheck);
        return row;
    }

    private async Task CommitSpeakerVolumeAsync(string ip, int percent)
    {
        try { await _sonos.SetSpeakerVolumeAsync(ip, percent); } catch { /* non-fatal; next Refresh will show the true value */ }
    }

    private void PopulateRooms()
    {
        _suppressRoomChange = true;
        RoomComboBox.Items.Clear();
        foreach (var group in _sonos.Groups)
            RoomComboBox.Items.Add(group);

        var active = _settings.ActiveRoom ?? _sonos.ActiveRoom;
        var match = _sonos.Groups.FirstOrDefault(g =>
            string.Equals(g.CoordinatorRoom, active, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            RoomComboBox.SelectedItem = match;
        else if (RoomComboBox.Items.Count > 0)
            RoomComboBox.SelectedIndex = 0;
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
        catch
        {
            // Leave titles empty; the user can Refresh once a room is reachable.
        }

        for (var i = 0; i < _favCombos.Length; i++)
            PopulateFavoriteCombo(_favCombos[i], titles, _settings.FavoriteSlots[i].FavoriteName);

        if (titles.Count == 0)
            SetStatus("No playable favorites found. Add a Sonos favorite/playlist, then Refresh.", warn: true);
    }

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

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Discovering Sonos devices…", warn: false);
        RefreshButton.IsEnabled = false;
        try
        {
            var preferred = (RoomComboBox.SelectedItem as SonosGroup)?.CoordinatorRoom;
            await _sonos.RefreshAsync(preferred);
            PopulateRooms();
            await LoadFavoritesAsync();
            await LoadSpeakerVolumesAsync();
            SetStatus($"Found {_sonos.Groups.Count} group(s).", warn: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Discovery failed: {ex.Message}", warn: true);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

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
        CaptureWindowGeometry();

        // Copy working values back into the live settings object.
        _settings.LevelVolumes = _levelVolumes;
        _settings.FreshStart = _freshStart;
        _settings.ShuffleLibrary = _shuffle;
        _settings.PlayPause = _playPause;
        _settings.Next = _next;
        _settings.Previous = _previous;
        _settings.VolumeUp = _volumeUp;
        _settings.VolumeDown = _volumeDown;
        _settings.Mute = _mute;
        if (int.TryParse(VolumeStepBox.Text, out var step) && step is >= 1 and <= 50)
            _settings.VolumeStep = step;
        if (int.TryParse(LevelPercentBox.Text, out var level) && level is >= 0 and <= 100)
            _settings.LevelVolumePercent = level;
        _settings.ShowFlyoutOnTrackChange = FlyoutOnTrackChangeCheckBox.IsChecked == true;
        _settings.ShowFlyoutOnAction = FlyoutOnActionCheckBox.IsChecked == true;
        _settings.NightlyResetEnabled = NightlyResetCheckBox.IsChecked == true;
        if (TryParseHhmm(NightlyResetTimeBox.Text, out var minutes))
            _settings.NightlyResetMinutes = minutes;
        if (RoomComboBox.SelectedItem is SonosGroup group)
            _settings.ActiveRoom = group.CoordinatorRoom;

        for (var i = 0; i < _favCombos.Length; i++)
        {
            _settings.FavoriteSlots[i].Hotkey = _favHotkeys[i];
            var name = _favCombos[i].SelectedItem as string;
            _settings.FavoriteSlots[i].FavoriteName = string.Equals(name, NoneLabel, StringComparison.Ordinal) ? null : name;
        }

        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save settings: {ex.Message}", warn: true);
            return;
        }

        var failures = _applyBindings();
        if (failures.Count > 0)
        {
            SetStatus($"Saved, but these hotkeys are in use elsewhere: {string.Join(", ", failures)}", warn: true);
            return;
        }

        SetStatus("Saved. Hotkeys are active.", warn: false);
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureWindowGeometry();
        try { _store.Save(_settings); } catch { /* non-fatal */ }
        HideToTrayRequested?.Invoke(this, EventArgs.Empty);
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
        try { _store.Save(_settings); } catch { /* non-fatal */ }
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
