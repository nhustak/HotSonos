using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

/// <summary>Address, port, and PIN shared by the first-launch Connect screen and More.</summary>
sealed class ConnectionForm
{
    public readonly Entry Host = RemoteUi.Field("PC address or 192.168.1.10");
    public readonly Entry Port = RemoteUi.Field("Port (blank = 42342)", Keyboard.Numeric);
    public readonly Entry Pin = RemoteUi.Field("6-digit PIN", Keyboard.Numeric);
    public readonly Label Status = RemoteUi.Muted("Enter the address and PIN from the PC Phone window.");

    public ConnectionForm()
    {
        Pin.MaxLength = 6;
        Load();
    }

    public void Load()
    {
        var saved = AppServices.Connection;
        Host.Text = saved.Host;
        Port.Text = saved.Port.ToString();
        Pin.Text = saved.Pin;
    }

    public bool TryRead(out string host, out int port, out string pin, out string? error)
    {
        if (!RemoteNet.TryParseAddress(Host.Text, Port.Text, out host, out port, out error))
        {
            pin = "";
            return false;
        }

        pin = (Pin.Text ?? "").Trim();
        if (!RemoteNet.IsSixDigitPin(pin))
        {
            error = "PIN must be 6 digits.";
            return false;
        }

        error = null;
        return true;
    }

    public async Task<RemoteCall<RemoteStatusResponse>?> ProbeAsync()
    {
        if (!TryRead(out var host, out var port, out var pin, out var error))
        {
            Status.Text = error;
            return null;
        }

        try
        {
            AppServices.Client.Apply(host, port, pin);
            Status.Text = "Connecting…";
            var call = await AppServices.Client.GetStatusAsync(CancellationToken.None);
            Status.Text = call.Kind == RemoteCallKind.Ok
                ? "Connected to " + host + ". " + (call.Value?.NowPlaying?.Title ?? call.Value?.ActiveGroupLabel ?? "Nothing playing yet.")
                : call.Detail ?? "Could not reach the PC.";
            return call;
        }
        catch (Exception ex)
        {
            Status.Text = FaultText(ex);
            return null;
        }
    }

    public bool Save()
    {
        if (!TryRead(out var host, out var port, out var pin, out var error))
        {
            Status.Text = error;
            return false;
        }

        try
        {
            var connection = AppServices.Connection;
            connection.Host = host;
            connection.Port = port;
            connection.Pin = pin;
            connection.Save();
            AppServices.Client.Apply(host, port, pin);
            return true;
        }
        catch (Exception ex)
        {
            Status.Text = FaultText(ex);
            return false;
        }
    }

    static string FaultText(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? "Could not connect." : ex.Message;
}
