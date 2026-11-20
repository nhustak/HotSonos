using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class PairPage : ContentPage
{
    readonly ConnectionForm _form = new();

    public PairPage()
    {
        Title = "Connect";
        BackgroundColor = Swatches.Bg;
        _form.Status.Text = "Connect to the HotSonos PC for full control. The address and PIN are on the PC Phone window.";
        Content = RemoteUi.ConnectPage(
            "Connect to your PC",
            _form.Status,
            RemoteUi.Muted("Find PC looks on this Wi-Fi. An emulator cannot hear that announce — type the PC's 192.168 address."),
            RemoteUi.Button("Find PC", FindAsync),
            _form.Host,
            _form.Port,
            _form.Pin,
            RemoteUi.Button("Test and connect", ConnectAsync),
            RemoteUi.Muted("Test and connect uses PC only, so a missed PC stays a missed PC. Auto and Speakers are in Settings → Connection / PC after you are in."),
            RemoteUi.Button("Use speakers without the PC", SpeakersAsync));
    }

    async Task FindAsync()
    {
        _form.Status.Text = "Looking for HotSonos on this network…";
        try
        {
            var found = await PcFinder.FindAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            if (found is null)
            {
                _form.Status.Text = "No PC answered. Type the address from the Phone window. On an emulator, type the PC's 192.168 address.";
                return;
            }

            _form.Host.Text = found.Value.Host;
            _form.Port.Text = found.Value.Port.ToString();
            _form.Status.Text = "Found HotSonos at " + found.Value.Host + ". Enter the 6-digit PIN, then Test and connect.";
        }
        catch (Exception ex)
        {
            _form.Status.Text = "Could not search the network. " + ex.Message;
        }
    }

    async Task ConnectAsync()
    {
        try
        {
            var call = await _form.ProbeAsync();
            if (call is not { Kind: RemoteCallKind.Ok })
                return;
            if (!_form.Save())
                return;
            AppServices.Link.SetPreference(PhoneLinkPreference.Pc);
            App.EnterApp();
        }
        catch (Exception ex)
        {
            _form.Status.Text = string.IsNullOrWhiteSpace(ex.Message) ? "Could not connect." : ex.Message;
        }
    }

    Task SpeakersAsync()
    {
        AppServices.Link.SetPreference(PhoneLinkPreference.Standalone);
        App.EnterApp();
        return Task.CompletedTask;
    }
}
