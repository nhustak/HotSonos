using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HotSonos.App.Library;
using HotSonos.App.Models;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace HotSonos.App.Windows;

/// <summary>
/// Overlay: digit keys / click toggle catalog tags on the now-playing track.
/// Stays open until Esc so multiple tags can be set; shows current labels.
/// </summary>
public partial class QuickTagOverlay : Window
{
    private readonly LibraryService _library;
    private readonly AppSettings _settings;
    private readonly string? _path;
    private readonly bool _canTag;
    private bool _closing;
    private bool _busy;
    private LibraryTrack? _track;

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

        RefreshTrackState();
    }

    private void RefreshTrackState()
    {
        if (_canTag && !string.IsNullOrWhiteSpace(_path))
            _track = _library.GetTrack(_path!) ?? _library.FindBySonosUri(_path);

        CurrentTagsText.Text = LibraryService.FormatCurrentTags(_track, _settings);
        TagList.ItemsSource = _settings.Tags
            .Select((t, i) => TagRow.From(t, i, _track))
            .ToList();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseOverlay();
            return;
        }

        if (_busy)
        {
            e.Handled = true;
            return;
        }

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

        if (index < 0 || index >= _settings.Tags.Count)
            return;

        e.Handled = true;
        ApplyKey(_settings.Tags[index].Key);
    }

    private void TagButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (sender is not Button { Tag: string key })
            return;
        ApplyKey(key);
    }

    private void ApplyKey(string tagKey)
    {
        if (_busy)
            return;

        if (!_canTag || string.IsNullOrWhiteSpace(_path))
        {
            ShowStatus("Cannot tag — no library path for now playing.", warn: true);
            return;
        }

        var def = _settings.FindTag(tagKey);
        var label = def?.Label ?? tagKey;

        _busy = true;
        TagList.IsEnabled = false;
        ShowStatus($"Toggling “{label}”…", warn: false);
        Cursor = Cursors.Wait;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var result = _library.SetTagFlag(
                    _path!, tagKey, forceEnable: null,
                    dryRun: false, updateMaster: _settings.TagUpdateMasterDefault);
                if (!result.Ok)
                {
                    ShowStatus(result.Error ?? result.Message ?? "Tag failed", warn: true);
                    ResetBusy();
                    return;
                }

                if (result.TrackAfter is not null)
                    _track = result.TrackAfter;
                else
                    RefreshTrackState();

                if (result.TrackAfter is not null)
                {
                    CurrentTagsText.Text = LibraryService.FormatCurrentTags(result.TrackAfter, _settings);
                    TagList.ItemsSource = _settings.Tags
                        .Select((t, i) => TagRow.From(t, i, result.TrackAfter))
                        .ToList();
                }
                else
                {
                    RefreshTrackState();
                }

                if (result.Queued)
                {
                    ShowStatus(
                        $"Queued “{label}” — file in use (playing). Will write when free. Esc closes.",
                        warn: false);
                }
                else
                {
                    ShowStatus($"{result.Message} (Esc to close)", warn: false);
                }
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, warn: true);
            }
            finally
            {
                ResetBusy();
            }
        }), DispatcherPriority.Background);
    }

    private void ResetBusy()
    {
        _busy = false;
        TagList.IsEnabled = true;
        Cursor = Cursors.Arrow;
    }

    private void ShowStatus(string message, bool warn)
    {
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = message;
        StatusText.Foreground = warn
            ? new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x40))
            : new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
    }

    private void CloseOverlay()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); }
        catch { /* ignore */ }
    }

    private sealed class TagRow
    {
        public string Key { get; init; } = "";
        public string Digit { get; init; } = "";
        public string Label { get; init; } = "";
        public string ActiveMark { get; init; } = "";
        public string ToolTip { get; init; } = "";
        public Brush RowBackground { get; init; } = new SolidColorBrush(Color.FromRgb(0x1C, 0x25, 0x36));
        public Brush RowBorder { get; init; } = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5C));

        public static TagRow From(TagDefinition t, int index, LibraryTrack? track)
        {
            var active = track is not null && track.HasTagKey(t.Key);
            return new TagRow
            {
                Key = t.Key,
                Digit = index < 9 ? (index + 1).ToString() : "·",
                Label = t.Label,
                ActiveMark = active ? "● on" : "",
                ToolTip = active
                    ? $"“{t.Label}” is on — press to turn off"
                    : $"“{t.Label}” — press to turn on",
                RowBackground = new SolidColorBrush(active
                    ? Color.FromRgb(0x1A, 0x3D, 0x2E)
                    : Color.FromRgb(0x1C, 0x25, 0x36)),
                RowBorder = new SolidColorBrush(active
                    ? Color.FromRgb(0x2E, 0xCC, 0x71)
                    : Color.FromRgb(0x3A, 0x4A, 0x5C)),
            };
        }
    }
}
