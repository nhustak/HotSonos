using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HotSonos.App.Models;
using HotSonos.Core.Models;

namespace HotSonos.App.Windows;

/// <summary>
/// Custom Now-Playing flyout: album art + title/artist + a status line. Draggable
/// (position persists), pinnable (stays on-screen), otherwise auto-dismisses.
/// </summary>
public partial class NowPlayingFlyout : Window
{
    private static readonly HttpClient ArtHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly AppSettings _settings;
    private readonly Action _persist;
    private readonly DispatcherTimer _dismissTimer;
    private NowPlaying? _current;
    private int _artGeneration;

    public NowPlayingFlyout(AppSettings settings, Action persist)
    {
        _settings = settings;
        _persist = persist;
        InitializeComponent();

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _dismissTimer.Tick += (_, _) => { _dismissTimer.Stop(); if (!_settings.FlyoutPinned) Hide(); };

        Loaded += (_, _) => PinButton.IsChecked = _settings.FlyoutPinned;
    }

    /// <summary>Refreshes the card with a track and shows it (per pin/timer rules).</summary>
    public void ShowNowPlaying(NowPlaying nowPlaying)
    {
        _current = nowPlaying;
        TitleText.Text = nowPlaying.IsEmpty ? "Nothing playing" : nowPlaying.Title;
        ArtistText.Text = nowPlaying.Artist ?? "";
        StatusText.Text = StateLabel(nowPlaying.State);
        SetArt(nowPlaying.AlbumArtUri);
        Reveal();
    }

    /// <summary>Shows a transient action line over the current track card.</summary>
    public void ShowAction(string message)
    {
        if (_current is not null)
        {
            TitleText.Text = _current.IsEmpty ? "Nothing playing" : _current.Title;
            ArtistText.Text = _current.Artist ?? "";
            SetArt(_current.AlbumArtUri);
        }
        StatusText.Text = message;
        Reveal();
    }

    private void Reveal()
    {
        if (!IsVisible)
        {
            Show();
            PositionFlyout();
        }
        _dismissTimer.Stop();
        if (!_settings.FlyoutPinned)
            _dismissTimer.Start();
    }

    private void PositionFlyout()
    {
        UpdateLayout();
        if (_settings.FlyoutLeft is { } left && _settings.FlyoutTop is { } top)
        {
            Left = left;
            Top = top;
            return;
        }

        var work = SystemParameters.WorkArea;
        const double margin = 24;
        Left = work.Right - ActualWidth - margin;
        Top = work.Bottom - ActualHeight - margin;
    }

    private async void SetArt(string? uri)
    {
        // Fetch the bytes ourselves and decode from a stream. Loading a BitmapImage
        // directly from a remote UriSource downloads async and can raise an
        // *unhandled* exception on the UI thread if the art fails — which would
        // crash the app. This keeps every failure inside the try/catch.
        var generation = ++_artGeneration;
        if (string.IsNullOrEmpty(uri))
        {
            ArtImage.Source = null;
            return;
        }

        try
        {
            var bytes = await ArtHttp.GetByteArrayAsync(uri).ConfigureAwait(true);
            if (generation != _artGeneration)
                return; // a newer track superseded this load

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            ArtImage.Source = bmp;
        }
        catch
        {
            if (generation == _artGeneration)
                ArtImage.Source = null;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        _dismissTimer.Stop(); // don't let a slow drag get auto-dismissed mid-move
        DragMove(); // blocks until the mouse is released
        _settings.FlyoutLeft = Left;
        _settings.FlyoutTop = Top;
        _persist();
        if (!_settings.FlyoutPinned)
            _dismissTimer.Start();
    }

    private void PinButton_Changed(object sender, RoutedEventArgs e)
    {
        _settings.FlyoutPinned = PinButton.IsChecked == true;
        _persist();

        if (_settings.FlyoutPinned)
            _dismissTimer.Stop();
        else
            _dismissTimer.Start();
    }

    public void HardClose()
    {
        _dismissTimer.Stop();
        Close();
    }

    private static string StateLabel(SonosTransportState state) => state switch
    {
        SonosTransportState.Playing => "▶ Playing",
        SonosTransportState.PausedPlayback => "⏸ Paused",
        SonosTransportState.Stopped => "⏹ Stopped",
        SonosTransportState.Transitioning => "… loading",
        _ => "",
    };
}
