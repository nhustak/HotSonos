using System.Windows;
using HotSonos.App.Models;
using HotSonos.App.Remote;

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
        if (!string.IsNullOrWhiteSpace(live))
        {
            StatusText.Text = "The phone can reach this PC while HotSonos is running.";
            HostText.Text = live;
        }
        else
        {
            var locals = string.Join(", ", HotSonosRemoteHost.LocalIpv4());
            StatusText.Text = string.IsNullOrWhiteSpace(locals)
                ? "Not listening yet. HotSonos waits until a speaker is on this PC's network."
                : "Not listening yet. HotSonos waits until a speaker shares a network with this PC. Addresses: " + locals;
            HostText.Text = $"http://<this-pc>:{settings.RemotePort}";
        }

        PortText.Text = settings.RemotePort.ToString();
        PinText.Text = settings.RemotePin ?? "";
        FirewallText.Text =
            "New-NetFirewallRule -DisplayName \"HotSonos Remote\" -Direction Inbound -Protocol TCP " +
            $"-LocalPort {settings.RemotePort} -RemoteAddress LocalSubnet -Action Allow -Profile Private";
    }

    private void NewPin_Click(object sender, RoutedEventArgs e) => _newPin();

    private void Firewall_Click(object sender, RoutedEventArgs e) => _firewall();
}
