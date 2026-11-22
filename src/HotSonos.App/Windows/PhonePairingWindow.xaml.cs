using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HotSonos.App.Infrastructure;
using HotSonos.App.Models;
using HotSonos.App.Remote;
using HotSonos.App.Services;
using HotSonos.Remote.Protocol;

namespace HotSonos.App;

public partial class PhonePairingWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<string?> _endpoint;
    private readonly Action _newPin;
    private readonly Action _firewall;
    private readonly Func<PairingPasswordRecord, string?> _changePassword;
    private bool _adbBusy;
    private bool _pairBusy;
    private string? _pairLink;

    public PhonePairingWindow(
        AppSettings settings,
        Func<string?> endpoint,
        Action newPin,
        Action firewall,
        Func<PairingPasswordRecord, string?> changePassword)
    {
        _settings = settings;
        _endpoint = endpoint;
        _newPin = newPin;
        _firewall = firewall;
        _changePassword = changePassword;
        InitializeComponent();
        _settings.EnsureShape();
        AdbHostBox.Text = _settings.PhoneAdbHost;
        AdbConnectPortBox.Text = _settings.PhoneAdbPort.ToString(CultureInfo.InvariantCulture);
        Refresh();
    }

    public void Refresh()
    {
        var settings = _settings.EnsureShape();
        var live = _endpoint();
        var locals = HotSonosRemoteHost.LocalIpv4();
        if (!string.IsNullOrWhiteSpace(live) && Uri.TryCreate(live, UriKind.Absolute, out var uri))
        {
            StatusText.Text = "The phone can reach this PC while HotSonos is running. Scan the code, pair with the password, or type this address and PIN.";
            HostText.Text = uri.Host;
            PortText.Text = uri.Port.ToString();
            UrlText.Text = "Listening at " + live + ". The phone also looks for a UDP announce on port " + RemoteDiscovery.Port + ".";
            ShowPairingCode(uri.Host, uri.Port, settings.RemotePin);
            var others = locals.Where(a => !string.Equals(a, uri.Host, StringComparison.OrdinalIgnoreCase)).ToList();
            OtherAddressesText.Text = others.Count == 0
                ? ""
                : "Other addresses on this PC are not the phone listener: " + string.Join(", ", others);
        }
        else
        {
            StatusText.Text = locals.Count == 0
                ? "Not listening yet. HotSonos waits until a speaker is on this PC's network."
                : "Not listening yet. HotSonos waits until a speaker shares a network with this PC, then binds that address.";
            HostText.Text = locals.Count == 0 ? "(no LAN address yet)" : string.Join("   ", locals);
            PortText.Text = settings.RemotePort.ToString();
            UrlText.Text = "When a speaker is found, type the address that shares the speaker's network.";
            OtherAddressesText.Text = "";
            ShowPairingCode(null, settings.RemotePort, settings.RemotePin);
        }

        PinText.Text = settings.RemotePin ?? "";
        FirewallText.Text =
            "New-NetFirewallRule -DisplayName \"HotSonos Remote\" -Direction Inbound -Protocol TCP " +
            $"-LocalPort {settings.RemotePort} -Action Allow -Profile Private" + Environment.NewLine +
            "New-NetFirewallRule -DisplayName \"HotSonos Remote Discover\" -Direction Inbound -Protocol UDP " +
            $"-LocalPort {RemoteDiscovery.Port} -Action Allow -Profile Private";
    }

    private void ShowPairingCode(string? host, int port, string? pin)
    {
        _pairLink = null;
        if (string.IsNullOrWhiteSpace(host) || !PhonePairLink.TryFormat(host, port, pin, out var link, out _))
        {
            QrBorder.Visibility = Visibility.Collapsed;
            QrImage.Source = null;
            CopyLinkButton.Visibility = Visibility.Collapsed;
            QrHint.Text = "The code appears when HotSonos is listening for the phone. Until then, type the address and PIN below.";
            return;
        }

        try
        {
            QrImage.Source = PairingQr.Create(link);
            QrBorder.Visibility = Visibility.Visible;
            CopyLinkButton.Visibility = Visibility.Visible;
            _pairLink = link;
            QrHint.Text = "Scan this from Connect, or from Settings → Connection / PC. Copy link puts the same address, port, and PIN on the clipboard as a hotsonos://pair link the phone can paste.";
        }
        catch
        {
            QrBorder.Visibility = Visibility.Collapsed;
            QrImage.Source = null;
            CopyLinkButton.Visibility = Visibility.Collapsed;
            QrHint.Text = "The pairing code could not be drawn. Type the address and PIN below.";
        }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pairLink))
            return;
        System.Windows.Clipboard.SetText(_pairLink);
    }

    private async void ChangePairPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_pairBusy)
            return;

        var password = PairPasswordBox.Password;
        var confirm = PairPasswordConfirmBox.Password;
        if (!PairingPassword.TryValidate(password, out var error))
        {
            PairPasswordStatus.Text = error;
            return;
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            PairPasswordStatus.Text = "Those passwords do not match.";
            return;
        }

        _pairBusy = true;
        ChangePairPasswordButton.IsEnabled = false;
        try
        {
            var record = await Task.Run(() => PairingPassword.Hash(password)).ConfigureAwait(true);
            var saveError = _changePassword(record);
            if (saveError is null)
            {
                PairPasswordBox.Clear();
                PairPasswordConfirmBox.Clear();
                PairPasswordStatus.Text = "Password changed. Every phone needs to pair again.";
                Refresh();
            }
            else
            {
                PairPasswordStatus.Text = saveError;
            }
        }
        finally
        {
            _pairBusy = false;
            ChangePairPasswordButton.IsEnabled = true;
        }
    }

    private void NewPin_Click(object sender, RoutedEventArgs e) => _newPin();

    private void Firewall_Click(object sender, RoutedEventArgs e) => _firewall();

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var text = (HostText.Text ?? "").Trim();
        if (text.Length == 0 || text.StartsWith('('))
            return;
        var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        System.Windows.Clipboard.SetText(first);
    }

    private void CopyPin_Click(object sender, RoutedEventArgs e)
    {
        var pin = (PinText.Text ?? "").Trim();
        if (pin.Length == 0)
            return;
        System.Windows.Clipboard.SetText(pin);
    }

    private async void AdbPair_Click(object sender, RoutedEventArgs e)
    {
        if (_adbBusy)
            return;
        if (!TryReadEndpoint(out var host, out var port, out var error))
        {
            ShowAdbResult(error, warn: true);
            return;
        }

        if (!PhoneWirelessDebug.TryValidatePort(AdbPairPortBox.Text, out var pairPort, out var portError))
        {
            ShowAdbResult("Pairing port: " + portError, warn: true);
            return;
        }

        if (!PhoneWirelessDebug.TryValidatePairingCode(AdbPairCodeBox.Text, out var code, out var codeError))
        {
            ShowAdbResult(codeError ?? PhoneWirelessDebug.CodeError, warn: true);
            return;
        }

        if (!TrySaveEndpoint(host, port, out error))
        {
            ShowAdbResult(error, warn: true);
            return;
        }

        var adb = PhoneWirelessDebug.ResolveAdb();
        if (adb is null)
        {
            ShowAdbResult(PhoneWirelessDebug.MissingAdbMessage, warn: true);
            return;
        }

        AdbPairCodeBox.Text = "";
        await RunAdbAsync(
            adb,
            PhoneWirelessDebug.PairArguments(host, pairPort, code),
            PhoneWirelessDebug.PairDisplay(host, pairPort),
            code).ConfigureAwait(true);
    }

    private async void AdbConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_adbBusy)
            return;
        if (!TryReadEndpoint(out var host, out var port, out var error))
        {
            ShowAdbResult(error, warn: true);
            return;
        }

        if (!TrySaveEndpoint(host, port, out error))
        {
            ShowAdbResult(error, warn: true);
            return;
        }

        var adb = PhoneWirelessDebug.ResolveAdb();
        if (adb is null)
        {
            ShowAdbResult(PhoneWirelessDebug.MissingAdbMessage, warn: true);
            return;
        }

        await RunAdbAsync(
            adb,
            PhoneWirelessDebug.ConnectArguments(host, port),
            PhoneWirelessDebug.ConnectDisplay(host, port),
            secret: null).ConfigureAwait(true);
    }

    private bool TryReadEndpoint(out string host, out int port, out string error)
    {
        if (!PhoneWirelessDebug.TryValidateHost(AdbHostBox.Text, out host, out var hostError))
        {
            port = 0;
            error = hostError ?? PhoneWirelessDebug.HostRequiredError;
            return false;
        }

        if (!PhoneWirelessDebug.TryValidatePort(AdbConnectPortBox.Text, out port, out var portError))
        {
            error = "Connect port: " + portError;
            return false;
        }

        error = "";
        return true;
    }

    private bool TrySaveEndpoint(string host, int port, out string error)
    {
        _settings.PhoneAdbHost = host;
        _settings.PhoneAdbPort = port;
        try
        {
            new ConfigStore().Save(_settings);
            AdbHostBox.Text = _settings.PhoneAdbHost;
            AdbConnectPortBox.Text = _settings.PhoneAdbPort.ToString(CultureInfo.InvariantCulture);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not save the phone adb address: " + ex.Message;
            return false;
        }
    }

    private async Task RunAdbAsync(string adb, IReadOnlyList<string> arguments, string display, string? secret)
    {
        _adbBusy = true;
        AdbPairButton.IsEnabled = false;
        AdbConnectButton.IsEnabled = false;
        ShowAdbResult("Running " + display, warn: false);
        try
        {
            var result = await PhoneWirelessDebug.ExecuteAsync(
                adb,
                arguments,
                display,
                secret,
                AppLog.Info).ConfigureAwait(true);
            if (!IsLoaded)
                return;
            ShowAdbResult(result.Text, warn: !result.Ok);
        }
        catch (Exception ex)
        {
            if (!IsLoaded)
                return;
            var message = PhoneWirelessDebug.Redact(display + Environment.NewLine + ex.Message, secret);
            AppLog.Info(message);
            ShowAdbResult(message, warn: true);
        }
        finally
        {
            _adbBusy = false;
            if (IsLoaded)
            {
                AdbPairButton.IsEnabled = true;
                AdbConnectButton.IsEnabled = true;
            }
        }
    }

    private void ShowAdbResult(string? text, bool warn)
    {
        AdbResultText.Text = text ?? "";
        AdbResultText.Foreground = warn
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x2B, 0x2B))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1C, 0x25, 0x36));
    }
}
