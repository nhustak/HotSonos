using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class LibraryPage : RemotePage
{
    readonly Entry _query = RemoteUi.Field("Search title, artist, T:, A:, TG:, F:");
    readonly Entry _newTag = RemoteUi.Field("New tag label");
    readonly Label _status = RemoteUi.Muted("");
    readonly VerticalStackLayout _results = new();
    readonly VerticalStackLayout _tags = new();
    int _offset;

    public LibraryPage()
    {
        Content = RemoteUi.Page(
            "Library",
            _status,
            _query,
            RemoteUi.Button("Search", () => SearchAsync(reset: true)),
            _results,
            RemoteUi.Button("Load older", () => SearchAsync(reset: false)),
            RemoteUi.Button("Rescan", () => RescanAsync(false), danger: true),
            RemoteUi.Button("Force rescan", () => RescanAsync(true), danger: true),
            _newTag,
            RemoteUi.Button("Create tag", CreateTagAsync),
            RemoteUi.Muted("Tags — delete removes the key from every file"),
            _tags);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = SearchAsync(reset: true);
        _ = LoadTagsAsync();
    }

    async Task SearchAsync(bool reset)
    {
        if (reset)
            _offset = 0;
        var call = await AppServices.Client.SearchAsync(_query.Text ?? "", 25, _offset, CancellationToken.None);
        if (reset)
            _results.Clear();
        if (call.Value is not { } page)
        {
            _status.Text = call.Detail ?? "Search failed.";
            return;
        }

        _offset += page.Tracks.Count;
        _status.Text = page.Tracks.Count == 0 ? "No more tracks." : $"{_offset} shown";
        foreach (var track in page.Tracks)
            _results.Add(TrackCard(track));
    }

    View TrackCard(RemoteTrackDto track)
    {
        var play = RemoteUi.Button("Play", () => Act(ct => AppServices.Client.PlayTrackAsync(track.Path, track.Title, track.Artist, ct), _status));
        var queue = RemoteUi.Button("Queue", () => Act(ct => AppServices.Client.QueueTrackAsync(track.Path, track.Title, track.Artist, ct), _status));
        var wipe = RemoteUi.Button("Wipe queue", () => WipeAsync(track), danger: true);
        var row = new Grid
        {
            ColumnSpacing = 6,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() },
        };
        Grid.SetColumn(play, 0);
        Grid.SetColumn(queue, 1);
        Grid.SetColumn(wipe, 2);
        row.Add(play);
        row.Add(queue);
        row.Add(wipe);
        return RemoteUi.Card(
            new Label { Text = track.Title ?? track.Path, TextColor = Swatches.Ink, FontAttributes = FontAttributes.Bold },
            RemoteUi.Muted($"{track.Artist} · {track.Album}"),
            RemoteUi.Muted(string.IsNullOrWhiteSpace(track.Tags) ? track.Genre ?? "" : track.Tags!),
            row,
            RemoteUi.Button("Toggle a tag…", () => ToggleAsync(track)));
    }

    async Task WipeAsync(RemoteTrackDto track)
    {
        if (!await Confirm("Wipe the queue", $"Replace the queue with {track.Title ?? "this track"}? The current queue is cleared."))
            return;
        await Act(ct => AppServices.Client.ReplaceTrackAsync(track.Path, track.Title, track.Artist, ct), _status);
    }

    async Task ToggleAsync(RemoteTrackDto track)
    {
        var tags = await AppServices.Client.GetTagsAsync(CancellationToken.None);
        var labels = tags.Value?.Tags.Select(t => t.Label).ToArray() ?? [];
        if (labels.Length == 0)
        {
            await DisplayAlert("Tags", "No tags yet.", "OK");
            return;
        }

        var pick = await DisplayActionSheet("Toggle tag", "Cancel", null, labels);
        var tag = tags.Value?.Tags.FirstOrDefault(t => t.Label == pick);
        if (tag is null)
            return;
        await Act(ct => AppServices.Client.ToggleTagAsync(track.Path, tag.Key, ct), _status);
        await SearchAsync(reset: true);
    }

    async Task RescanAsync(bool force)
    {
        var message = force
            ? "Re-read tags for every file? This can take a long time."
            : "Rescan the library for new and changed files?";
        if (!await Confirm(force ? "Force rescan" : "Rescan", message))
            return;
        await Act(ct => AppServices.Client.RescanAsync(force, ct), _status);
    }

    async Task CreateTagAsync()
    {
        await Act(ct => AppServices.Client.CreateTagAsync(_newTag.Text ?? "", ct), _status);
        _newTag.Text = "";
        await LoadTagsAsync();
    }

    async Task LoadTagsAsync()
    {
        var call = await AppServices.Client.GetTagsAsync(CancellationToken.None);
        _tags.Clear();
        foreach (var tag in call.Value?.Tags ?? [])
        {
            var key = tag.Key;
            var label = tag.Label;
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition() } };
            var rename = RemoteUi.Button(label, () => RenameAsync(key, label));
            var delete = RemoteUi.Button("Delete", () => DeleteAsync(key, label), danger: true);
            Grid.SetColumn(rename, 0);
            Grid.SetColumn(delete, 1);
            row.Add(rename);
            row.Add(delete);
            _tags.Add(row);
        }
    }

    async Task RenameAsync(string key, string label)
    {
        var next = await DisplayPromptAsync("Rename tag", "New label. Files keep the same key.", initialValue: label);
        if (string.IsNullOrWhiteSpace(next))
            return;
        await Act(ct => AppServices.Client.RenameTagAsync(key, next.Trim(), ct), _status);
        await LoadTagsAsync();
    }

    async Task DeleteAsync(string key, string label)
    {
        if (!await Confirm("Delete tag", $"Delete {label} and strip it from every file that has it?"))
            return;
        await Act(ct => AppServices.Client.DeleteTagAsync(key, ct), _status);
        await LoadTagsAsync();
    }
}
