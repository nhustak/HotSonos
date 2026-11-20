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
            _connection.Host,
            _connection.Port,
            _connection.Pin,
            RemoteUi.Button("Find PC", FindPcAsync),
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
    }

    async Task FindPcAsync()
    {
        _connection.Status.Text = "Looking for HotSonos on this network…";
        try
        {
            var found = await PcFinder.FindAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            if (found is null)
            {
                _connection.Status.Text = "No PC answered. Type the address from the Phone window.";
                return;
            }

            _connection.Host.Text = found.Value.Host;
            _connection.Port.Text = found.Value.Port.ToString();
            _connection.Status.Text = "Found " + found.Value.Host + ". Enter the PIN, then Test and save.";
        }
        catch (Exception ex)
        {
            _connection.Status.Text = ex.Message;
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
