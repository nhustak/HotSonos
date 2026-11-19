using System.Windows;
using HotSonos.App.Models;
using HotSonos.App.Remote;
using HotSonos.Remote.Protocol;

namespace HotSonos.App;

public partial class PhonePairingWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<string?> _endpoint;
    private readonly Action _newPin;
    private readonly Action _firewall;

    public PhonePairingWindow(AppSettings settings, Func<string?> endpoint, Action newPin, Action firewall)
    {
        _settings = settings;
        _endpoint = endpoint;
        _newPin = newPin;
        _firewall = firewall;
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
}
