using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace HotSonos.Remote.Views;

/// <summary>Camera preview that returns the first HotSonos pairing QR, or null if cancelled.</summary>
sealed class PairScanPage : ContentPage
{
    readonly TaskCompletionSource<string?> _done = new();
    readonly CameraBarcodeReaderView _reader;
    readonly Label _hint;
    bool _finished;

    PairScanPage()
    {
        Title = "Scan QR";
        BackgroundColor = Colors.Black;
#if IOS
        SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.Container;
#endif
        _reader = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormat.QrCode,
                AutoRotate = true,
                TryHarder = true,
                Multiple = false,
            },
            CameraLocation = CameraLocation.Rear,
            IsDetecting = true,
        };
        _reader.BarcodesDetected += OnDetected;
        _reader.HandlerChanged += (_, _) => _ = ChooseCameraAsync();

        _hint = new Label
        {
            Text = "Point at the code on the PC Phone window.",
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center,
            FontSize = 16,
        };
        var cancel = RemoteUi.Button("Cancel", () => Finish(null));
        var overlay = new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(16, 12, 16, 24),
            BackgroundColor = Color.FromArgb("#E612161C"),
            VerticalOptions = LayoutOptions.End,
            Children = { _hint, cancel },
        };

        Content = new Grid
        {
            Children = { _reader, overlay },
        };
    }

    public static async Task<string?> ScanAsync(Page host)
    {
        var page = new PairScanPage();
        await host.Navigation.PushModalAsync(page).ConfigureAwait(true);
        return await page._done.Task.ConfigureAwait(true);
    }

    async Task ChooseCameraAsync()
    {
        if (_finished || _reader.Handler is null)
            return;
        try
        {
            var cameras = await _reader.GetAvailableCameras().ConfigureAwait(true);
            if (cameras.Count == 0 || _finished)
                return;
            var rear = cameras.FirstOrDefault(c => c.Location == CameraLocation.Rear);
            var pick = rear ?? cameras[0];
            _reader.SelectedCamera = pick;
            _reader.CameraLocation = pick.Location;
        }
        catch
        {
            // The view still opens the default camera when the list cannot be read.
        }
    }

    void OnDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_finished)
            return;
        var value = e.Results?.Select(result => result.Value).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (!HotSonos.Remote.Protocol.PhonePairLink.TryParse(value, out _, out _, out _, out _))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_finished)
                    _hint.Text = "That code is not a HotSonos pairing link.";
            });
            return;
        }

        var text = value.Trim();
        MainThread.BeginInvokeOnMainThread(() => _ = Finish(text));
    }

    async Task Finish(string? value)
    {
        if (_finished)
            return;
        _finished = true;
        _reader.IsDetecting = false;
        _reader.BarcodesDetected -= OnDetected;
        try
        {
            await Navigation.PopModalAsync().ConfigureAwait(true);
        }
        catch
        {
            // Already dismissed.
        }

        _done.TrySetResult(value);
    }

    protected override void OnDisappearing()
    {
        if (!_finished)
        {
            _finished = true;
            _reader.IsDetecting = false;
            _reader.BarcodesDetected -= OnDetected;
            _done.TrySetResult(null);
        }

        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = Finish(null);
        return true;
    }
}
