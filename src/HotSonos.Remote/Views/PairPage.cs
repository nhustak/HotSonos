using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class PairPage : ContentPage
{
    readonly Entry _host = RemoteUi.Field("PC address or http://192.168.1.10:42342");
    readonly Entry _port = RemoteUi.Field("Port", Keyboard.Numeric);
    readonly Entry _pin = RemoteUi.Field("6-digit PIN", Keyboard.Numeric);
    readonly Label _status = RemoteUi.Muted("Enter the address and PIN from the PC Phone button.");

    public PairPage()
    {
        Title = "Pair";
        BackgroundColor = Swatches.Bg;
        var saved = AppServices.Connection;
        _host.Text = saved.Host;
        _port.Text = saved.Port.ToString();
        _pin.Text = saved.Pin;
        _pin.MaxLength = 6;

        Content = RemoteUi.Page(
            "Pair with the PC",
            _status,
            RemoteUi.Muted("Link"),
            RemoteUi.ModePicker(),
            RemoteUi.Muted("On the PC, open HotSonos and tap Phone. The emulator on that PC is 10.0.2.2. Speakers only skips the PC."),
            _host,
            _port,
            _pin,
            RemoteUi.Button("Test connection", TestAsync),
            RemoteUi.Button("Save and open", SaveAsync),
            RemoteUi.Button("Use speakers without the PC", SpeakersAsync));
    }

    async Task TestAsync()
    {
        if (!TryRead(out var host, out var port, out var pin, out var error))
        {
            _status.Text = error;
            return;
        }

        AppServices.Client.Apply(host, port, pin);
        _status.Text = "Connecting…";
        var call = await AppServices.Client.GetStatusAsync(CancellationToken.None);
        _status.Text = call.Kind switch
        {
            RemoteCallKind.Ok => "Connected. " + (call.Value?.NowPlaying?.Title ?? call.Value?.ActiveGroupLabel ?? "No music yet."),
            RemoteCallKind.Unauthorized => "The PC rejected the PIN.",
            _ => "Cannot reach the PC.",
        };
    }

    async Task SaveAsync()
    {
        if (!TryRead(out var host, out var port, out var pin, out var error))
        {
            _status.Text = error;
            return;
        }

        var connection = AppServices.Connection;
        connection.Host = host;
        connection.Port = port;
        connection.Pin = pin;
        connection.Save();
        App.EnterApp();
        await Task.CompletedTask;
    }

    Task SpeakersAsync()
    {
        AppServices.Link.SetPreference(PhoneLinkPreference.Standalone);
        App.EnterApp();
        return Task.CompletedTask;
    }

    bool TryRead(out string host, out int port, out string pin, out string? error)
    {
        if (!RemoteNet.TryParseAddress(_host.Text, _port.Text, out host, out port, out error))
        {
            pin = "";
            return false;
        }

        pin = (_pin.Text ?? "").Trim();
        if (!RemoteNet.IsSixDigitPin(pin))
        {
            error = "PIN must be 6 digits.";
            return false;
        }

        error = null;
        return true;
    }
}
