using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class PlayPage : RemotePage
{
    readonly VerticalStackLayout _tags = new();
    readonly VerticalStackLayout _genres = new();
    readonly VerticalStackLayout _folders = new();
    readonly Label _status = RemoteUi.Muted("");

    public PlayPage()
    {
        Content = RemoteUi.Page(
            "Play",
            _status,
            RemoteUi.Button("Refresh", LoadAsync),
            RemoteUi.Muted("Tags"),
            _tags,
            RemoteUi.Muted("Genres"),
            _genres,
            RemoteUi.Muted("Folders"),
            _folders);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    async Task LoadAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _tags.Clear();
            _genres.Clear();
            _folders.Clear();
            _tags.Add(RemoteUi.NeedsPc("Tags, genres, and library folders"));
            _status.Text = "Those play sources are the PC library. Sonos favorites are on Now.";
            return;
        }

        var call = await AppServices.Client.GetSourcesAsync(CancellationToken.None);
        _tags.Clear();
        _genres.Clear();
        _folders.Clear();
        if (call.Value is not { } sources)
        {
            _status.Text = call.Detail ?? "Could not load play sources.";
            return;
        }

        _status.Text = sources.ControlSourceLabel;
        foreach (var tag in sources.Tags)
        {
            var key = tag.Key;
            _tags.Add(RemoteUi.Button(tag.Label, () => Act(ct => AppServices.Client.PlayTagAsync(key, ct), _status)));
        }

        if (sources.Genres.Count == 0)
            _genres.Add(RemoteUi.Muted("Genres are hidden, or the library has not been scanned."));
        foreach (var genre in sources.Genres)
        {
            var name = genre.Name;
            _genres.Add(RemoteUi.Button($"{name} ({genre.Count})", () => Act(ct => AppServices.Client.PlayGenreAsync(name, ct), _status)));
        }

        foreach (var folder in sources.Folders)
        {
            var path = folder.Path;
            var mark = folder.InDaily ? " · Daily" : "";
            _folders.Add(RemoteUi.Button($"{folder.Name} ({folder.Tracks}){mark}", () => Act(ct => AppServices.Client.PlayFolderAsync(path, ct), _status)));
        }
    }
}
