using System.Windows;
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
    private readonly Action _saveSettings;
    private bool _adbFieldsLoaded;

    public PhonePairingWindow(AppSettings settings, Func<string?> endpoint, Action newPin, Action firewall)
        : this(settings, endpoint, newPin, firewall, () => new ConfigStore().Save(settings))
    {
    }

    public PhonePairingWindow(
        AppSettings settings,
        Func<string?> endpoint,
        Action newPin,
        Action firewall,
        Action saveSettings)
    {
        _settings = settings;
        _endpoint = endpoint;
        _newPin = newPin;
        _firewall = firewall;
        _saveSettings = saveSettings;
        InitializeComponent();
        Refresh();
    }

    public void Refresh()
    {
        var settings = _settings.EnsureShape();
        var live = _endpoint();
        var locals = HotSonosRemoteHost.LocalIpv4();
        if (!string.IsNullOrWhiteSpace(live) && Uri.TryCreate(live, UriKind.Absolute, out var uri))
        {
            StatusText.Text = "The phone can reach this PC while HotSonos is running. Type this address and PIN on the Connect screen.";
            HostText.Text = uri.Host;
            PortText.Text = uri.Port.ToString();
            UrlText.Text = "Listening at " + live + ". The phone also looks for a UDP announce on port " + RemoteDiscovery.Port + ".";
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
        }

        PinText.Text = settings.RemotePin ?? "";
        if (!_adbFieldsLoaded)
        {
            AdbHostBox.Text = settings.PhoneAdbHost;
            AdbPortBox.Text = settings.PhoneAdbPort.ToString();
            _adbFieldsLoaded = true;
        }

        FirewallText.Text =
            "New-NetFirewallRule -DisplayName \"HotSonos Remote\" -Direction Inbound -Protocol TCP " +
            $"-LocalPort {settings.RemotePort} -Action Allow -Profile Private" + Environment.NewLine +
            "New-NetFirewallRule -DisplayName \"HotSonos Remote Discover\" -Direction Inbound -Protocol UDP " +
            $"-LocalPort {RemoteDiscovery.Port} -Action Allow -Profile Private";
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

    private async void AdbConnect_Click(object sender, RoutedEventArgs e)
    {
        AdbConnectButton.IsEnabled = false;
        try
        {
            if (!PhoneAdb.TryParse(AdbHostBox.Text, AdbPortBox.Text, out var host, out var port, out var error))
            {
                ShowAdbResult(false, error ?? PhoneAdb.BadPortError);
                return;
            }

            AdbHostBox.Text = host;
            AdbPortBox.Text = port.ToString();
            if (!TrySaveAdb(host, port, out var saveError))
            {
                ShowAdbResult(false, saveError ?? "Could not save settings.");
                return;
            }

            var adb = PhoneAdb.FindAdb();
            if (adb is null)
            {
                ShowAdbResult(false, PhoneAdb.NotFoundMessage());
                return;
            }

            AdbResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x70, 0x7F));
            AdbResultText.Text = "Connecting to " + PhoneAdb.FormatEndpoint(host, port) + "…";
            var result = await Task.Run(() => PhoneAdb.ConnectAsync(adb, host, port));
            if (!IsLoaded)
                return;
            ShowAdbResult(result.Connected, result.DisplayText);
        }
        catch (Exception ex)
        {
            if (IsLoaded)
                ShowAdbResult(false, ex.Message);
        }
        finally
        {
            if (IsLoaded)
                AdbConnectButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (PhoneAdb.TryParse(AdbHostBox.Text, AdbPortBox.Text, out var host, out var port, out _)
                && (!string.Equals(host, _settings.PhoneAdbHost, StringComparison.Ordinal)
                    || port != _settings.PhoneAdbPort))
            {
                TrySaveAdb(host, port, out _);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Phone wireless debugging address was not saved", ex);
        }

        base.OnClosed(e);
    }

    private bool TrySaveAdb(string host, int port, out string? error)
    {
        _settings.PhoneAdbHost = host;
        _settings.PhoneAdbPort = port;
        try
        {
            _saveSettings();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not save settings. " + ex.Message;
            return false;
        }
    }

    private void ShowAdbResult(bool connected, string message)
    {
        var marker = connected ? "connected" : "failed";
        AdbResultText.Text = message.Contains(marker, StringComparison.OrdinalIgnoreCase)
            ? message
            : (connected ? "Connected. " : "Failed. ") + message;
        AdbResultText.Foreground = new System.Windows.Media.SolidColorBrush(connected
            ? System.Windows.Media.Color.FromRgb(0x1C, 0x8E, 0x54)
            : System.Windows.Media.Color.FromRgb(0xB4, 0x23, 0x18));
    }
}
