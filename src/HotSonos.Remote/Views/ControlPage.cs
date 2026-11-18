using HotSonos.Core;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class ControlPage : RemotePage
{
    readonly Image _art = new() { HeightRequest = 160, WidthRequest = 160, Aspect = Aspect.AspectFill, HorizontalOptions = LayoutOptions.Start };
    readonly Label _banner = RemoteUi.Muted("");
    readonly Label _title = new() { TextColor = Swatches.Ink, FontSize = 20, FontAttributes = FontAttributes.Bold };
    readonly Label _artist = RemoteUi.Muted("");
    readonly Label _meta = RemoteUi.Muted("");
    readonly Label _volume = RemoteUi.Muted("Volume");
    readonly Slider _slider = new() { Minimum = 0, Maximum = 100, Value = 20 };
    readonly Picker _rooms = new() { Title = "Room", TextColor = Swatches.Ink, BackgroundColor = Swatches.Card };
    readonly Label _daily = RemoteUi.Muted("Daily group");
    readonly Switch _allSpeakers = new();
    readonly VerticalStackLayout _roomChecks = new();
    readonly VerticalStackLayout _presets = new();
    readonly Entry _presetName = RemoteUi.Field("Group name");
    readonly VerticalStackLayout _from = new();
    readonly VerticalStackLayout _slots = new();
    readonly VerticalStackLayout _favorites = new();
    readonly VerticalStackLayout _spotify = new();
    readonly Entry _spotifyLink = RemoteUi.Field("Spotify link");
    readonly VerticalStackLayout _heard = new();
    readonly Label _action = RemoteUi.Muted("");
    readonly Slider _seek = new() { Minimum = 0, Maximum = 100, IsVisible = false };
    readonly VerticalStackLayout _queue = new();

    bool _drag;
    bool _seekDrag;
    bool _suppressRoom;
    bool _suppressAll;
    string _roomKey = "";
    string? _artRevision;
    RemoteStatusResponse? _status;
    RemoteDailyGroupsResponse? _groups;

    public ControlPage()
    {
        _slider.DragStarted += (_, _) => _drag = true;
        _slider.DragCompleted += async (_, _) =>
        {
            _drag = false;
            var percent = (int)_slider.Value;
            if (AppServices.Link.OnPc)
                await Act(ct => AppServices.Client.SetVolumeAsync(percent, ct), _action);
            else
                await Speaker((_, ct) => AppServices.Link.Speakers.SetHouseVolumeAsync(percent, ct), _action);
        };
        _seek.DragStarted += (_, _) => _seekDrag = true;
        _seek.DragCompleted += async (_, _) =>
        {
            _seekDrag = false;
            var now = AppServices.Link.Speakers.LastNow;
            if (AppServices.Link.OnPc || now?.Duration is not { TotalSeconds: > 0 } dur)
                return;
            var target = TimeSpan.FromSeconds(dur.TotalSeconds * (_seek.Value / 100));
            await Speaker((speaker, ct) => speaker.SeekToTimeAsync(target, ct), _action);
        };
        _rooms.SelectedIndexChanged += async (_, _) =>
        {
            if (_suppressRoom || _status is null)
                return;
            var index = _rooms.SelectedIndex;
            if (index < 0 || index >= _status.Groups.Count)
                return;
            var room = _status.Groups[index].Room;
            if (AppServices.Link.OnPc)
                await Act(ct => AppServices.Client.SetRoomAsync(room, ct), _action);
            else
                AppServices.Link.SelectRoom(room);
        };
        _allSpeakers.Toggled += async (_, e) =>
        {
            if (_suppressAll)
                return;
            if (!AppServices.Link.OnPc)
            {
                _suppressAll = true;
                _allSpeakers.IsToggled = !e.Value;
                _suppressAll = false;
                _action.Text = "Daily speaker groups need the PC.";
                return;
            }

            await Act(ct => AppServices.Client.SelectDailyAsync(new RemoteDailySelectBody { All = e.Value }, ct), _action);
            await LoadDailyAsync();
        };

        var transport = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
        Add(transport, RemoteUi.Button("Previous", () => Go(
            (speaker, ct) => speaker.PreviousAsync(ct),
            ct => AppServices.Client.PreviousAsync(ct))), 0);
        Add(transport, RemoteUi.Button("Play / Pause", () => Go(
            async (speaker, ct) => { await speaker.PlayPauseAsync(ct); },
            ct => AppServices.Client.PlayPauseAsync(ct))), 1);
        Add(transport, RemoteUi.Button("Next", () => Go(
            (speaker, ct) => speaker.NextAsync(ct),
            ct => AppServices.Client.NextAsync(ct))), 2);

        var volumeRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() } };
        Add(volumeRow, RemoteUi.Button("Mute", () => Go(
            async (_, ct) => { await AppServices.Link.Speakers.ToggleGroupMuteAsync(ct); },
            ct => AppServices.Client.MuteAsync(ct))), 0);
        Add(volumeRow, RemoteUi.Button("Level all", () => Go(
            (_, ct) => AppServices.Link.Speakers.SetHouseVolumeAsync((int)_slider.Value, ct),
            ct => AppServices.Client.LevelAsync(ct))), 1);

        Content = RemoteUi.Page(
            "Now",
            _banner,
            _art,
            _title,
            _artist,
            _meta,
            _seek,
            transport,
            _volume,
            _slider,
            volumeRow,
            RemoteUi.Muted("Room"),
            _rooms,
            RemoteUi.Card(
                RemoteUi.Muted("Daily speaker group"),
                _daily,
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { _allSpeakers, new Label { Text = "All speakers", TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center } },
                },
                _presets,
                _roomChecks,
                _presetName,
                RemoteUi.Button("Save checked rooms as a group", SavePresetAsync)),
            RemoteUi.Button("Shuffle speaker library", ShuffleLibraryAsync),
            RemoteUi.Muted("Queue"),
            _queue,
            RemoteUi.Muted("Start shuffle from"),
            _from,
            RemoteUi.Button("Start shuffle", () => PcOnly("HotSonos shuffle") ? Act(ct => AppServices.Client.StartShuffleAsync(ct), _action) : Task.CompletedTask),
            RemoteUi.Button("Daily shuffle", () => PcOnly("Daily shuffle") ? Act(ct => AppServices.Client.PlayDailyAsync(ct), _action) : Task.CompletedTask),
            RemoteUi.Button("Fresh start", FreshStartAsync, danger: true),
            RemoteUi.Button("Resume shuffle", () => PcOnly("Resume shuffle") ? Act(ct => AppServices.Client.ResumeShuffleAsync(ct), _action) : Task.CompletedTask),
            RemoteUi.Muted("Favorite slots"),
            _slots,
            RemoteUi.Muted("Sonos favorites"),
            _favorites,
            RemoteUi.Muted("Spotify"),
            _spotifyLink,
            RemoteUi.Button("Play Spotify link", () => Act(ct => AppServices.Client.PlaySpotifyAsync(_spotifyLink.Text ?? "", ct), _action)),
            RemoteUi.Button("Save Spotify link", SaveSpotifyAsync),
            _spotify,
            RemoteUi.Muted("Heard outside the library"),
            _heard,
            _action,
            RemoteUi.Button("Refresh lists", LoadListsAsync));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadListsAsync();
    }

    protected override void OnLinkChanged() => _ = LoadListsAsync();

    Task Go(Func<SonosController, CancellationToken, Task> speakers, Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>> pc) =>
        AppServices.Link.OnPc ? Act(pc, _action) : Speaker(speakers, _action);

    protected override void OnStatus(RemoteStatusResponse? status)
    {
        _status = status;
        if (!string.IsNullOrWhiteSpace(AppServices.Live.Problem))
            _banner.Text = AppServices.Live.Problem;
        else if (status?.WakeActive == true)
            _banner.Text = "Wake is running";
        else if (status?.Busy == true)
            _banner.Text = "The PC is busy";
        else
            _banner.Text = status?.ActiveGroupLabel ?? "";

        var np = status?.NowPlaying;
        _title.Text = string.IsNullOrWhiteSpace(np?.Title) ? "Nothing playing" : np!.Title;
        _artist.Text = string.Join(" — ", new[] { np?.Artist, np?.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _meta.Text = np is null
            ? ""
            : $"{np.State} · {np.Source} · {np.Track}/{np.Tracks}";
        if (status?.Volume is int volume)
        {
            _volume.Text = status.Muted == true ? $"Volume {volume}% · muted" : $"Volume {volume}%";
            if (!_drag)
                _slider.Value = volume;
        }

        ApplyRooms(status);
        if (!AppServices.Link.OnPc && !_seekDrag)
        {
            var playing = AppServices.Link.Speakers.LastNow;
            _seek.IsVisible = playing?.Duration is { TotalSeconds: > 1 };
            if (playing?.Duration is { TotalSeconds: > 0 } dur && playing.Position is { } pos)
                _seek.Value = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds * 100, 0, 100);
        }
        else if (AppServices.Link.OnPc)
        {
            _seek.IsVisible = false;
        }

        if (np?.ArtRevision != _artRevision)
            _ = LoadArtAsync(np?.ArtRevision, np?.HasArt == true);
    }

    async Task ShuffleLibraryAsync()
    {
        if (AppServices.Link.OnPc)
        {
            _action.Text = "Start shuffle uses play history on the PC. Speaker shuffle is for standalone.";
            return;
        }

        _action.Text = "Picking a random sample from the speaker library…";
        await Speaker((speaker, ct) => speaker.PlayRandomLibrarySampleAsync(40, ct), _action);
    }

    async Task LoadListsAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            await LoadSpeakerListsAsync();
            return;
        }

        await LoadDailyAsync();
        var sources = await AppServices.Client.GetSourcesAsync(CancellationToken.None);
        _slots.Clear();
        _from.Clear();
        if (sources.Value is { } src)
        {
            _action.Text = "Shuffle from " + src.ControlSourceLabel;
            _from.Add(RemoteUi.Button("All · Music Library", () => SetSourceAsync("all")));
            foreach (var tag in src.Tags)
            {
                var key = tag.Key;
                _from.Add(RemoteUi.Button("Tag · " + tag.Label, () => SetSourceAsync("tag:" + key)));
            }

            foreach (var genre in src.Genres)
            {
                var name = genre.Name;
                _from.Add(RemoteUi.Button("Genre · " + name, () => SetSourceAsync("genre:" + name)));
            }

            foreach (var folder in src.Folders)
            {
                var path = folder.Path;
                _from.Add(RemoteUi.Button("Folder · " + folder.Name, () => SetSourceAsync("folder:" + path)));
            }

            foreach (var slot in src.Slots)
            {
                var index = slot.Index;
                _slots.Add(RemoteUi.Button($"{slot.Index}. {slot.Label}", () => Act(ct => AppServices.Client.PlaySlotAsync(index, ct), _action)));
            }
        }

        var favorites = await AppServices.Client.GetFavoritesAsync(CancellationToken.None);
        _favorites.Clear();
        foreach (var fav in favorites.Value?.Favorites ?? [])
        {
            var name = fav.Name;
            _favorites.Add(RemoteUi.Button(name, () => Act(ct => AppServices.Client.PlayFavoriteAsync(name, ct), _action)));
        }

        var spotify = await AppServices.Client.GetSpotifyAsync(CancellationToken.None);
        _spotify.Clear();
        foreach (var link in spotify.Value?.Links ?? [])
        {
            var reference = link.Reference;
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition() } };
            Add(row, RemoteUi.Button(link.Name, () => Act(ct => AppServices.Client.PlaySpotifyAsync(reference, ct), _action)), 0);
            Add(row, RemoteUi.Button("Remove", () => RemoveSpotifyAsync(reference), danger: true), 1);
            _spotify.Add(row);
        }

        var heard = await AppServices.Client.GetHeardAsync(CancellationToken.None);
        _heard.Clear();
        foreach (var track in heard.Value?.Tracks ?? [])
            _heard.Add(RemoteUi.Muted($"{track.Title} — {track.Artist} · {track.Plays}"));
    }

    async Task LoadSpeakerListsAsync()
    {
        _daily.Text = "Daily speaker groups need the PC.";
        _from.Clear();
        _from.Add(RemoteUi.NeedsPc("HotSonos shuffle (play history, tags, genres, folders)"));
        _slots.Clear();
        _slots.Add(RemoteUi.NeedsPc("Favorite slots"));
        _spotify.Clear();
        _spotify.Add(RemoteUi.NeedsPc("Saved Spotify links"));
        _heard.Clear();
        _heard.Add(RemoteUi.NeedsPc("Heard outside the library"));
        _presets.Clear();
        _roomChecks.Clear();
        _favorites.Clear();
        _queue.Clear();

        var speaker = AppServices.Link.Speakers.Controller;
        if (speaker is null)
        {
            _action.Text = AppServices.Link.Problem ?? "No speakers yet.";
            return;
        }

        try
        {
            var favorites = await speaker.GetFavoritesAsync(CancellationToken.None);
            foreach (var fav in favorites.Where(f => f.IsPlayable))
            {
                var title = fav.Title;
                _favorites.Add(RemoteUi.Button(title, () => Speaker((s, ct) => s.PlayFavoriteByNameAsync(title, ct), _action)));
            }

            var queue = await speaker.GetQueueAsync(30, CancellationToken.None);
            if (queue.Count == 0)
                _queue.Add(RemoteUi.Muted("Queue is empty."));
            foreach (var track in queue)
            {
                var number = track.Number;
                _queue.Add(RemoteUi.Button($"{number}. {track.Title} — {track.Artist}", () =>
                    Speaker((s, ct) => s.SeekToTrackAsync(number, ct), _action)));
            }

            _action.Text = "Sonos favorites and the speaker queue. HotSonos shuffle needs the PC.";
        }
        catch (Exception ex)
        {
            _action.Text = ex.Message;
        }
    }

    async Task SetSourceAsync(string token)
    {
        if (!PcOnly("HotSonos shuffle"))
            return;
        await Act(ct => AppServices.Client.SetShuffleSourceAsync(token, ct), _action);
        await LoadListsAsync();
    }

    async Task LoadDailyAsync()
    {
        if (!AppServices.Link.OnPc)
            return;
        var call = await AppServices.Client.GetDailyGroupsAsync(CancellationToken.None);
        _groups = call.Value;
        if (_groups is null)
            return;
        _suppressAll = true;
        _allSpeakers.IsToggled = _groups.AllSpeakers;
        _suppressAll = false;
        _daily.Text = _groups.AllSpeakers
            ? "All speakers join Daily shuffle, Fresh start, and regroup."
            : (_groups.ActivePreset ?? $"{_groups.Rooms.Count} rooms") + " · unchecked rooms stay out.";

        _presets.Clear();
        foreach (var preset in _groups.Presets)
        {
            var name = preset.Name;
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition() } };
            Add(row, RemoteUi.Button((preset.Active ? "On · " : "") + name, () => UsePresetAsync(name)), 0);
            Add(row, RemoteUi.Button("Remove", () => RemovePresetAsync(name), danger: true), 1);
            _presets.Add(row);
        }

        _roomChecks.Clear();
        var selected = new HashSet<string>(_groups.Rooms, StringComparer.OrdinalIgnoreCase);
        foreach (var room in _groups.KnownRooms)
        {
            var box = new CheckBox { IsChecked = selected.Contains(room), Color = Swatches.Accent };
            var label = new Label { Text = room, TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center };
            _roomChecks.Add(new HorizontalStackLayout { Spacing = 8, Children = { box, label } });
        }
    }

    async Task UsePresetAsync(string name)
    {
        if (!PcOnly("Daily speaker groups"))
            return;
        await Act(ct => AppServices.Client.SelectDailyAsync(new RemoteDailySelectBody { Preset = name, PresetOn = true }, ct), _action);
        await LoadDailyAsync();
    }

    async Task RemovePresetAsync(string name)
    {
        if (!PcOnly("Daily speaker groups"))
            return;
        if (!await Confirm("Remove group", $"Remove the Daily group {name}?"))
            return;
        await Act(ct => AppServices.Client.RemoveDailyAsync(name, ct), _action);
        await LoadDailyAsync();
    }

    async Task SavePresetAsync()
    {
        if (!PcOnly("Daily speaker groups"))
            return;
        var rooms = CheckedRooms();
        await Act(ct => AppServices.Client.SaveDailyAsync(_presetName.Text ?? "", rooms, ct), _action);
        _presetName.Text = "";
        await LoadDailyAsync();
    }

    List<string> CheckedRooms()
    {
        var rooms = new List<string>();
        foreach (var row in _roomChecks.Children.OfType<HorizontalStackLayout>())
        {
            var box = row.Children.OfType<CheckBox>().FirstOrDefault();
            var label = row.Children.OfType<Label>().FirstOrDefault();
            if (box?.IsChecked == true && !string.IsNullOrWhiteSpace(label?.Text))
                rooms.Add(label.Text);
        }

        return rooms;
    }

    bool PcOnly(string what)
    {
        if (AppServices.Link.OnPc)
            return true;
        _action.Text = what + " needs the PC.";
        return false;
    }

    async Task FreshStartAsync()
    {
        if (!PcOnly("Fresh start"))
            return;
        if (!await Confirm("Fresh start", "Re-discover speakers, regroup the Daily group, and start a new shuffle?"))
            return;
        await Act(ct => AppServices.Client.FreshStartAsync(ct), _action);
    }

    async Task SaveSpotifyAsync()
    {
        if (!PcOnly("Saved Spotify links"))
            return;
        await Act(ct => AppServices.Client.SaveSpotifyAsync(null, _spotifyLink.Text ?? "", ct), _action);
        _spotifyLink.Text = "";
        await LoadListsAsync();
    }

    async Task RemoveSpotifyAsync(string reference)
    {
        if (!await Confirm("Remove Spotify link", reference))
            return;
        await Act(ct => AppServices.Client.RemoveSpotifyAsync(reference, ct), _action);
        await LoadListsAsync();
    }

    void ApplyRooms(RemoteStatusResponse? status)
    {
        var groups = status?.Groups ?? [];
        var key = string.Join("|", groups.Select(g => g.Room));
        if (key == _roomKey)
            return;
        _roomKey = key;
        _suppressRoom = true;
        _rooms.Items.Clear();
        foreach (var group in groups)
            _rooms.Items.Add(string.IsNullOrWhiteSpace(group.Label) ? group.Room : group.Label);
        var index = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            if (string.Equals(groups[i].Room, status?.ActiveRoom, StringComparison.OrdinalIgnoreCase))
                index = i;
        }

        _rooms.SelectedIndex = groups.Count == 0 ? -1 : index;
        _suppressRoom = false;
    }

    async Task LoadArtAsync(string? revision, bool hasArt)
    {
        _artRevision = revision;
        if (!AppServices.Link.OnPc)
        {
            _art.Source = hasArt && Uri.TryCreate(revision, UriKind.Absolute, out var art) ? ImageSource.FromUri(art) : null;
            return;
        }

        if (!hasArt || string.IsNullOrEmpty(revision))
        {
            _art.Source = null;
            return;
        }

        var call = await AppServices.Client.GetArtAsync(CancellationToken.None);
        if (_artRevision != revision || call.Value is not { Length: > 0 } bytes)
            return;
        _art.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
    }

    static void Add(Grid grid, View view, int column)
    {
        Grid.SetColumn(view, column);
        grid.Add(view);
    }
}
