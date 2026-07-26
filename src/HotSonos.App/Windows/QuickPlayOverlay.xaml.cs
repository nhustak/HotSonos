using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HotSonos.App.Library;
using HotSonos.App.Models;
using HotSonos.App.Services;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace HotSonos.App.Windows;

/// <summary>
/// HotLaunch-style picker: digit 1 = full library shuffle; 2–9 = tags then Sonos favorites/playlists.
/// </summary>
public partial class QuickPlayOverlay : Window
{
    private readonly SonosManager _sonos;
    private readonly LibraryService? _library;
    private readonly AppSettings _settings;
    private readonly List<SourceRow> _sources = [];
    private bool _closing;
    private bool _busy;

    public QuickPlayOverlay(
        SonosManager sonos,
        LibraryService? library,
        AppSettings settings,
        IReadOnlyList<string>? sonosPlayableTitles = null)
    {
        InitializeComponent();
        _sonos = sonos;
        _library = library;
        _settings = settings.EnsureShape();
        BuildSources(sonosPlayableTitles ?? []);
        SourceList.ItemsSource = _sources;
    }

    private void BuildSources(IReadOnlyList<string> sonosTitles)
    {
        _sources.Clear();
        // Slot 1 always: full Music Library shuffle
        _sources.Add(SourceRow.Make(
            slot: 1,
            kind: SourceKind.LibraryShuffle,
            kindLabel: "All",
            title: "Shuffle Music Library",
            detail: "History-aware full library → all speakers",
            payload: ""));

        var slot = 2;
        foreach (var t in _settings.Tags)
        {
            if (slot > 9) break;
            var count = _library?.GetTracksWithTag(t.Key).Count ?? 0;
            _sources.Add(SourceRow.Make(
                slot: slot++,
                kind: SourceKind.Tag,
                kindLabel: "Tag",
                title: t.Label,
                detail: count == 0
                    ? "No tracks tagged yet"
                    : $"{count} track(s) · shuffled · top-up if enabled",
                payload: t.Key));
        }

        foreach (var title in sonosTitles)
        {
            if (slot > 9) break;
            if (string.IsNullOrWhiteSpace(title)) continue;
            _sources.Add(SourceRow.Make(
                slot: slot++,
                kind: SourceKind.Sonos,
                kindLabel: "Sonos",
                title: title,
                detail: "Sonos favorite or playlist",
                payload: title));
        }
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
        PlaySlot(slot);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Click-away closes (pick is one-shot; unlike QuickTag).
        if (!_busy)
            CloseOverlay();
    }

    private void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: int slot })
            return;
        PlaySlot(slot);
    }

    private void PlaySlot(int slot)
    {
        var src = _sources.FirstOrDefault(s => s.Slot == slot);
        if (src is null)
        {
            ShowStatus($"No source in slot {slot}.", warn: true);
            return;
        }

        _busy = true;
        SourceList.IsEnabled = false;
        ShowStatus($"Starting “{src.Title}”…", warn: false);
        Cursor = Cursors.Wait;

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                string toast;
                switch (src.Kind)
                {
                    case SourceKind.LibraryShuffle:
                        await _sonos.GroupAllSpeakersAsync().ConfigureAwait(true);
                        var summary = await _sonos.ShuffleWithHistoryAsync().ConfigureAwait(true);
                        toast = $"🔀 {summary}";
                        break;
                    case SourceKind.Tag:
                        if (_library is null)
                            throw new InvalidOperationException("Library service not available.");
                        toast = await _sonos.PlayTaggedTracksAsync(_library, src.Payload, shuffle: true)
                            .ConfigureAwait(true);
                        break;
                    case SourceKind.Sonos:
                        toast = await _sonos.PlaySonosFavoriteByNameAsync(src.Payload).ConfigureAwait(true);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown source kind.");
                }

                ShowStatus(toast, warn: false);
                // Brief feedback then close
                await Task.Delay(280).ConfigureAwait(true);
                CloseOverlay();
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, warn: true);
                _busy = false;
                SourceList.IsEnabled = true;
                Cursor = Cursors.Arrow;
            }
        }), DispatcherPriority.Background);
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

    private enum SourceKind
    {
        LibraryShuffle,
        Tag,
        Sonos,
    }

    private sealed class SourceRow
    {
        public int Slot { get; init; }
        public SourceKind Kind { get; init; }
        public string KindLabel { get; init; } = "";
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Payload { get; init; } = "";
        public Brush RowBackground { get; init; } = new SolidColorBrush(Color.FromRgb(0x1C, 0x25, 0x36));
        public Brush RowBorder { get; init; } = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5C));

        public static SourceRow Make(
            int slot, SourceKind kind, string kindLabel, string title, string detail, string payload)
        {
            var accent = kind == SourceKind.LibraryShuffle;
            return new SourceRow
            {
                Slot = slot,
                Kind = kind,
                KindLabel = kindLabel,
                Title = title,
                Detail = detail,
                Payload = payload,
                RowBackground = new SolidColorBrush(accent
                    ? Color.FromRgb(0x1A, 0x3D, 0x2E)
                    : Color.FromRgb(0x1C, 0x25, 0x36)),
                RowBorder = new SolidColorBrush(accent
                    ? Color.FromRgb(0x2E, 0xCC, 0x71)
                    : Color.FromRgb(0x3A, 0x4A, 0x5C)),
            };
        }
    }
}
