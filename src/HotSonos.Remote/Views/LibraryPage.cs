using HotSonos.Core.Models;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class LibraryPage : RemotePage
{
    readonly Entry _query = RemoteUi.Field("Search title, artist, T:, A:, TG:, F:");
    readonly Entry _newTag = RemoteUi.Field("New tag label");
    readonly Label _status = RemoteUi.Muted("");
    readonly VerticalStackLayout _results = new();
    readonly VerticalStackLayout _tags = new();
    readonly VerticalStackLayout _config = new() { Spacing = 6 };
    readonly Entry _master = RemoteUi.Field(@"Master folder, optional (\\server\masters)");
    readonly Entry _addRoot = RemoteUi.Field(@"Add folder (\\server\share\Music)");
    readonly List<(string Path, Switch Daily)> _roots = [];
    int _offset;

    public LibraryPage()
    {
        Content = RemoteUi.Page(
            "Library",
            _status,
            RemoteUi.Muted("Library on the PC"),
            _config,
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

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadConfigAsync();
        _ = SearchAsync(reset: true);
        _ = LoadTagsAsync();
    }

    protected override void OnLinkChanged()
    {
        _ = LoadConfigAsync();
        _ = SearchAsync(reset: true);
        _ = LoadTagsAsync();
    }

    async Task LoadConfigAsync()
    {
        _config.Clear();
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _config.Add(RemoteUi.NeedsPc("Library folders, master folder, rescan, and tags"));
            return;
        }

        var call = await AppServices.Client.GetLibraryConfigAsync(CancellationToken.None);
        var config = call.Value;
        if (config is null)
        {
            _config.Add(RemoteUi.Muted(call.Detail ?? "Could not load library settings."));
            return;
        }

        var scanning = config.Scanning ? " Scanning." : "";
        _config.Add(RemoteUi.Muted($"{config.TrackCount} tracks in the cache.{scanning} {config.Phase}"));
        if (!string.IsNullOrWhiteSpace(config.LastError))
            _config.Add(RemoteUi.Muted(config.LastError));
        if (!string.IsNullOrWhiteSpace(config.Note))
            _config.Add(RemoteUi.Muted(config.Note));
        _roots.Clear();
        foreach (var root in config.Roots)
        {
            var daily = new Switch { IsToggled = root.InDaily };
            _roots.Add((root.Path, daily));
            _config.Add(RootRow(daily, root.Path));
        }

        if (_roots.Count == 0)
            _config.Add(RemoteUi.Muted("No library folders yet. Discover from Sonos, or add a UNC path."));
        _master.Text = config.MasterRoot ?? "";
        _config.Add(RemoteUi.Muted("Checked folders are the Daily mix. A master folder is where tag writes are copied."));
        _config.Add(_master);
        _config.Add(_addRoot);
        _config.Add(RemoteUi.Button("Add folder", AddRootAsync));
        _config.Add(RemoteUi.Button("Save library folders", SaveRootsAsync));
        _config.Add(RemoteUi.Button("Discover from Sonos", DiscoverAsync));
    }

    Task AddRootAsync()
    {
        var path = (_addRoot.Text ?? "").Trim();
        if (path.Length == 0)
        {
            _status.Text = "Type a folder path first.";
            return Task.CompletedTask;
        }

        var daily = new Switch { IsToggled = true };
        _roots.Add((path, daily));
        _addRoot.Text = "";
        _config.Insert(0, RootRow(daily, path));
        _status.Text = "Added. Save library folders to keep it.";
        return Task.CompletedTask;
    }

    static View RootRow(Switch daily, string path)
    {
        var label = new Label
        {
            Text = path,
            TextColor = Swatches.Ink,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        var row = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
        };
        Grid.SetColumn(daily, 0);
        Grid.SetColumn(label, 1);
        row.Add(daily);
        row.Add(label);
        return row;
    }

    async Task SaveRootsAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Library folders");
            return;
        }

        if (_roots.Count == 0 && !await Confirm("Clear library folders", "Save an empty folder list on the PC?"))
            return;
        var body = new RemoteLibraryConfigBody
        {
            MasterRoot = _master.Text ?? "",
            Roots = _roots.Select(r => new RemoteLibraryRootBody { Path = r.Path, InDaily = r.Daily.IsToggled }).ToList(),
        };
        var call = await AppServices.Client.SaveLibraryConfigAsync(body, CancellationToken.None);
        _status.Text = call.Value?.Error ?? call.Value?.Note ?? call.Detail ?? (call.Kind == RemoteCallKind.Ok ? "Saved." : "Could not save.");
        if (call.Kind == RemoteCallKind.Ok && call.Value?.Ok == true)
            await LoadConfigAsync();
    }

    async Task DiscoverAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Discover from Sonos");
            return;
        }

        if (!await Confirm("Discover from Sonos", "Replace the library folder list with the folders Sonos has indexed?"))
            return;
        var call = await AppServices.Client.DiscoverLibraryAsync(CancellationToken.None);
        _status.Text = call.Value?.Error ?? call.Value?.Note ?? call.Detail ?? "Discover finished.";
        await LoadConfigAsync();
    }

    async Task SearchAsync(bool reset)
    {
        if (!AppServices.Link.DrivePc)
        {
            await SearchSpeakersAsync(reset);
            return;
        }

        if (!AppServices.Link.OnPc)
        {
            if (reset)
                _results.Clear();
            _status.Text = AppServices.Link.Problem ?? "Cannot reach the PC.";
            return;
        }

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

    async Task SearchSpeakersAsync(bool reset)
    {
        if (reset)
        {
            _offset = 0;
            _results.Clear();
        }

        var speaker = AppServices.Link.Speakers.Controller;
        if (speaker is null)
        {
            _status.Text = AppServices.Link.Problem ?? "No speakers yet.";
            return;
        }

        try
        {
            var tracks = await speaker.SearchLibraryAsync(_query.Text, _offset, 25, CancellationToken.None);
            _offset += tracks.Count;
            _status.Text = tracks.Count == 0
                ? "No more tracks in the speaker library."
                : $"{_offset} from the speaker library. Tags and rescan need the PC.";
            foreach (var track in tracks)
                _results.Add(SpeakerCard(track));
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    View SpeakerCard(SonosLibraryTrack track)
    {
        var play = RemoteUi.Button("Play", () => Speaker((s, ct) => s.InsertLibraryUriAndPlayNowAsync(track.Uri, track.Title, track.Artist, ct), _status));
        var queue = RemoteUi.Button("Queue", () => Speaker(async (s, ct) => { await s.InsertLibraryUriNextAsync(track.Uri, track.Title, track.Artist, ct); }, _status));
        var wipe = RemoteUi.Button("Wipe queue", () => WipeSpeakerAsync(track), danger: true);
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
            new Label { Text = track.Title, TextColor = Swatches.Ink, FontAttributes = FontAttributes.Bold },
            RemoteUi.Muted($"{track.Artist} · {track.Album}"),
            row);
    }

    async Task WipeSpeakerAsync(SonosLibraryTrack track)
    {
        if (!await Confirm("Wipe the queue", $"Replace the queue with {track.Title}? The current queue is cleared."))
            return;
        await Speaker((s, ct) => s.PlayLibraryUriAsync(track.Uri, track.Title, track.Artist, ct), _status);
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
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Rescan");
            return;
        }

        var message = force
            ? "Re-read tags for every file? This can take a long time."
            : "Rescan the library for new and changed files?";
        if (!await Confirm(force ? "Force rescan" : "Rescan", message))
            return;
        await Act(ct => AppServices.Client.RescanAsync(force, ct), _status);
    }

    async Task CreateTagAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Tags");
            return;
        }

        await Act(ct => AppServices.Client.CreateTagAsync(_newTag.Text ?? "", ct), _status);
        _newTag.Text = "";
        await LoadTagsAsync();
    }

    async Task LoadTagsAsync()
    {
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _tags.Clear();
            _tags.Add(RemoteUi.NeedsPc("Tags, rescan, and writing HOTSONOS_TAGS"));
            return;
        }

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
