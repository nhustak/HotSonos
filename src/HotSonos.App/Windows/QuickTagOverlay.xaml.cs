using System.Windows;
using System.Windows.Input;
using HotSonos.App.Library;
using HotSonos.App.Models;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace HotSonos.App.Windows;

/// <summary>
/// HotLaunch-style overlay: digit keys apply a tag preset to the playing library track.
/// </summary>
public partial class QuickTagOverlay : Window
{
    private readonly LibraryService _library;
    private readonly AppSettings _settings;
    private readonly string? _path;
    private readonly bool _canTag;
    private bool _closing;

    public QuickTagOverlay(
        LibraryService library,
        AppSettings settings,
        string? nowPlayingLine,
        string? path,
        string? resolveMessage)
    {
        InitializeComponent();
        _library = library;
        _settings = settings.EnsureShape();
        _path = path;
        _canTag = !string.IsNullOrWhiteSpace(path);

        NowPlayingText.Text = string.IsNullOrWhiteSpace(nowPlayingLine)
            ? "(nothing playing)"
            : nowPlayingLine;
        PathText.Text = _canTag
            ? path!
            : (resolveMessage ?? "Cannot tag this source (not a local library track in cache).");

        if (!_canTag)
            ShowStatus(resolveMessage ?? "Cannot tag.", warn: true);

        PresetList.ItemsSource = _settings.TagPresets.OrderBy(p => p.Slot).ToList();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseOverlay();
            return;
        }

        var slot = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => 0,
        };

        if (slot == 0)
            return;

        e.Handled = true;
        ApplySlot(slot);
    }

    private void ApplySlot(int slot)
    {
        if (!_canTag || string.IsNullOrWhiteSpace(_path))
        {
            ShowStatus("Cannot tag — no library path for now playing.", warn: true);
            return;
        }

        var result = _library.ApplyPreset(_path, slot, dryRun: false, updateMaster: _settings.TagUpdateMasterDefault);
        if (!result.Ok)
        {
            ShowStatus(result.Error ?? result.Message ?? "Tag failed", warn: true);
            return;
        }

        // Success: dismiss quickly (HotLaunch-style).
        CloseOverlay();
    }

    private void ShowStatus(string message, bool warn)
    {
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = message;
        StatusText.Foreground = warn
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xC0, 0x40))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xCC, 0x71));
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Click-away closes (same idea as a launcher overlay).
        if (!_closing)
            CloseOverlay();
    }

    private void CloseOverlay()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); }
        catch { /* ignore */ }
    }
}
