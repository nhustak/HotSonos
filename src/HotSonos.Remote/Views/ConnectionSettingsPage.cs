using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class ConnectionSettingsPage : RemotePage
{
    readonly ConnectionForm _connection = new();

    public ConnectionSettingsPage()
    {
        Content = RemoteUi.Page(
            "Connection / PC",
            RemoteUi.Back(),
            _connection.Status,
            RemoteUi.Muted("If Find PC misses, scan the code, paste the hotsonos://pair link, or type the address from the PC Phone window. This page is Settings, then Connection / PC. A Chromebook often cannot hear the UDP broadcast."),
            _connection.Host,
            _connection.Port,
            _connection.Pin,
            RemoteUi.Button("Find PC", FindPcAsync),
            _connection.ScanButton,
            _connection.PasteButton,
            RemoteUi.Muted("Scan QR uses the camera when this device has one, and stays hidden when it does not."),
            RemoteUi.Button("Test and save", SaveConnectionAsync),
            RemoteUi.Muted("Link"),
            RemoteUi.ModePicker(),
            RemoteUi.Button("Forget PC", ForgetAsync, danger: true),
            RemoteUi.Button("Use speakers without the PC", SpeakersAsync));
    }

    protected override void OnShown()
    {
        base.OnShown();
        _connection.Load();
        _connection.NoteShown();
    }

    protected override void OnHidden()
    {
        _connection.NoteHidden();
        base.OnHidden();
    }

    async Task FindPcAsync()
    {
        _connection.Status.Text = "Looking for HotSonos on this network…";
        try
        {
            var found = await PcFinder.FindAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            if (found is null)
            {
#if IOS
                _connection.Status.Text = HotSonos.Remote.IosDiscovery.FindPcMiss;
#else
                _connection.Status.Text = RemoteFailureText.FindPcMissed;
#endif
                return;
            }

            _connection.Host.Text = found.Value.Host;
            _connection.Port.Text = found.Value.Port.ToString();
            _connection.Status.Text = "Found " + found.Value.Host + ". Enter the PIN, then Test and save.";
        }
        catch (Exception ex)
        {
#if IOS
            _connection.Status.Text = HotSonos.Remote.IosDiscovery.FindPcMiss;
#else
            _connection.Status.Text = string.IsNullOrWhiteSpace(ex.Message)
                ? RemoteFailureText.FindPcMissed
                : ex.Message + " " + RemoteFailureText.FindPcMissed;
#endif
        }
    }

    async Task SaveConnectionAsync()
    {
        var call = await _connection.ProbeAsync();
        if (call is not { Kind: RemoteCallKind.Ok })
            return;
        if (!_connection.Save())
            return;
        if (AppServices.Link.Preference == PhoneLinkPreference.Standalone)
            AppServices.Link.SetPreference(PhoneLinkPreference.Pc);
        await AppServices.Link.ResolveAsync(CancellationToken.None);
    }

    async Task ForgetAsync()
    {
        if (!await Confirm("Forget PC", "Clear the saved address and PIN on this phone?"))
            return;
        AppServices.Connection.Forget();
        AppServices.Link.SetPreference(PhoneLinkPreference.Pc);
        _connection.Load();
        _connection.Status.Text = "Forgotten. Enter the address and PIN from the PC Phone window.";
        await AppServices.Link.ResolveAsync(CancellationToken.None);
    }

    Task SpeakersAsync()
    {
        AppServices.Link.SetPreference(PhoneLinkPreference.Standalone);
        _ = AppServices.Link.ResolveAsync(CancellationToken.None);
        _connection.Status.Text = "Speakers only. The PC is not contacted.";
        return Task.CompletedTask;
    }
}
