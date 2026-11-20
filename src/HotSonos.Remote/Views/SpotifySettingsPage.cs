using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class SpotifySettingsPage : RemotePage
{
    readonly Label _status = RemoteUi.Muted("");
    readonly Entry _spotifyLink = RemoteUi.Field("Spotify link");
    readonly VerticalStackLayout _spotify = new();

    public SpotifySettingsPage()
    {
        Content = RemoteUi.Page(
            "Spotify links",
            RemoteUi.Back(),
            _status,
            _spotifyLink,
            RemoteUi.Button("Play Spotify link", () => PcOnly("Saved Spotify links") ? Act(ct => AppServices.Client.PlaySpotifyAsync(_spotifyLink.Text ?? "", ct), _status) : Task.CompletedTask),
            RemoteUi.Button("Save Spotify link", SaveSpotifyAsync),
            _spotify,
            RemoteUi.Button("Refresh", LoadAsync));
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    async Task LoadAsync()
    {
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _spotify.Clear();
            _spotify.Add(RemoteUi.NeedsPc("Saved Spotify links"));
            _status.Text = AppServices.Link.DrivePc
                ? (AppServices.Link.Problem ?? "Cannot reach the PC.")
                : RemoteFailureText.PcRequired("Saved Spotify links");
            return;
        }

        var spotify = await AppServices.Client.GetSpotifyAsync(CancellationToken.None);
        _spotify.Clear();
        foreach (var link in spotify.Value?.Links ?? [])
        {
            var reference = link.Reference;
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition() } };
            var play = RemoteUi.Button(link.Name, () => Act(ct => AppServices.Client.PlaySpotifyAsync(reference, ct), _status));
            var remove = RemoteUi.Button("Remove", () => RemoveSpotifyAsync(reference), danger: true);
            Grid.SetColumn(play, 0);
            Grid.SetColumn(remove, 1);
            row.Add(play);
            row.Add(remove);
            _spotify.Add(row);
        }

        _status.Text = spotify.Value is null ? spotify.Detail ?? "Could not load Spotify links." : "";
    }

    async Task SaveSpotifyAsync()
    {
        if (!PcOnly("Saved Spotify links"))
            return;
        await Act(ct => AppServices.Client.SaveSpotifyAsync(null, _spotifyLink.Text ?? "", ct), _status);
        _spotifyLink.Text = "";
        await LoadAsync();
    }

    async Task RemoveSpotifyAsync(string reference)
    {
        if (!await Confirm("Remove Spotify link", reference))
            return;
        await Act(ct => AppServices.Client.RemoveSpotifyAsync(reference, ct), _status);
        await LoadAsync();
    }

    bool PcOnly(string what)
    {
        if (AppServices.Link.DrivePc)
            return true;
        _status.Text = RemoteFailureText.PcRequired(what);
        return false;
    }
}
