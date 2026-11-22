using System.Globalization;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

/// <summary>Address, port, and PIN shared by the first-launch Connect screen and Settings → Connection / PC.</summary>
sealed class ConnectionForm
{
    public readonly Entry Host = RemoteUi.Field("PC address or 192.168.1.10");
    public readonly Entry Port = RemoteUi.Field("Port (blank = 42342)", Keyboard.Numeric);
    public readonly Entry Pin = RemoteUi.Field("6-digit PIN", Keyboard.Numeric);
    public readonly Label Status = RemoteUi.Muted("Scan the code, paste the pairing link, or enter the address and PIN from the PC Phone window.");
    public readonly Button ScanButton;
    public readonly Button PasteButton;

    public ConnectionForm()
    {
        Pin.MaxLength = 6;
        ScanButton = RemoteUi.Button("Scan QR", ScanAsync);
        PasteButton = RemoteUi.Button("Paste pairing link", PasteAsync);
        ScanButton.IsVisible = false;
        Load();
    }

    public void Load()
    {
        var saved = AppServices.Connection;
        Host.Text = saved.Host;
        Port.Text = saved.Port.ToString(CultureInfo.InvariantCulture);
        Pin.Text = saved.Pin;
    }

    public void NoteShown()
    {
        ScanButton.IsVisible = PhoneCamera.IsAvailable;
        PairingHandoff.NoteShown(this);
    }

    public void NoteHidden() => PairingHandoff.NoteHidden(this);

    public void ApplyFields(string host, int port, string pin)
    {
        Host.Text = host;
        Port.Text = port.ToString(CultureInfo.InvariantCulture);
        Pin.Text = pin;
    }

    public bool TryRead(out string host, out int port, out string pin, out string? error)
    {
        var rawHost = Host.Text ?? "";
        if (rawHost.Contains("hotsonos:", StringComparison.OrdinalIgnoreCase))
        {
            if (!PhonePairLink.TryParse(rawHost, out host, out port, out pin, out error))
                return false;
            ApplyFields(host, port, pin);
            return true;
        }

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

    async Task ScanAsync()
    {
        if (!PhoneCamera.IsAvailable)
        {
            ScanButton.IsVisible = false;
            Status.Text = "This device has no camera. Paste the pairing link, or type the address and PIN.";
            return;
        }

        var page = HostPage(ScanButton);
        if (page is null)
            return;

        try
        {
            var granted = await PhoneCamera.EnsurePermissionAsync().ConfigureAwait(true);
            if (!granted)
            {
                Status.Text = "Camera permission is off. Paste the pairing link, or allow the camera for HotSonos and try Scan QR again.";
                return;
            }

            var text = await PairScanPage.ScanAsync(page).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(text))
                return;
            await PairingHandoff.ApplyAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status.Text = string.IsNullOrWhiteSpace(ex.Message) ? "Could not open the camera." : ex.Message;
        }
    }

    async Task PasteAsync()
    {
        string? text;
        try
        {
            if (!Clipboard.Default.HasText)
            {
                Status.Text = "The clipboard has no pairing link. Copy the link from the PC Phone window, or scan the code.";
                return;
            }

            text = await Clipboard.Default.GetTextAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status.Text = string.IsNullOrWhiteSpace(ex.Message)
                ? "Could not read the clipboard."
                : "Could not read the clipboard. " + ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Status.Text = "The clipboard has no pairing link. Copy the link from the PC Phone window, or scan the code.";
            return;
        }

        await PairingHandoff.ApplyAsync(text).ConfigureAwait(true);
    }

    static Page? HostPage(VisualElement start)
    {
        Element? current = start;
        while (current is not null)
        {
            if (current is Page page)
                return page;
            current = current.Parent;
        }

        return Application.Current?.MainPage;
    }

    static string FaultText(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? "Could not connect." : ex.Message;
}
