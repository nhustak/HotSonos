using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class PairPage : ContentPage
{
    readonly ConnectionForm _form = new();

    public PairPage()
    {
        Title = "Connect";
        BackgroundColor = Swatches.Bg;
#if IOS
        SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.Container;
#endif
        _form.Status.Text = "Connect to the HotSonos PC for full control. Scan the code on the PC Phone window, paste the pairing link, or type the address and PIN.";
        Content = RemoteUi.ConnectPage(
            "Connect to your PC",
            _form.Status,
            RemoteUi.Muted("Find PC looks on this Wi-Fi. A Chromebook or an emulator often cannot hear that announce — scan the code, paste the link, or type the address from the Phone window."),
            RemoteUi.Button("Find PC", FindAsync),
            _form.ScanButton,
            _form.PasteButton,
            RemoteUi.Muted("Scan QR uses the camera when this device has one, and stays hidden when it does not. Paste pairing link reads a hotsonos://pair link."),
            _form.Host,
            _form.Port,
            _form.Pin,
            RemoteUi.Button("Test and connect", ConnectAsync),
            RemoteUi.Muted("Test and connect uses PC only, so a missed PC stays a missed PC. Auto and Speakers are in Settings → Connection / PC after you are in."),
            RemoteUi.Button("Use speakers without the PC", SpeakersAsync));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _form.NoteShown();
    }

    protected override void OnDisappearing()
    {
        if (!ReferenceEquals(Application.Current?.MainPage, this))
            _form.NoteHidden();
        base.OnDisappearing();
    }

    async Task FindAsync()
    {
        _form.Status.Text = "Looking for HotSonos on this network…";
        try
        {
            var found = await PcFinder.FindAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            if (found is null)
            {
#if IOS
                _form.Status.Text = HotSonos.Remote.IosDiscovery.FindPcMiss;
#else
                _form.Status.Text = RemoteFailureText.FindPcMissed;
#endif
                return;
            }

            _form.Host.Text = found.Value.Host;
            _form.Port.Text = found.Value.Port.ToString();
            _form.Status.Text = "Found HotSonos at " + found.Value.Host + ". Enter the 6-digit PIN, then Test and connect.";
        }
        catch (Exception ex)
        {
#if IOS
            _form.Status.Text = HotSonos.Remote.IosDiscovery.FindPcMiss;
#else
            _form.Status.Text = "Could not search the network. " + ex.Message + " " + RemoteFailureText.FindPcMissed;
#endif
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
