using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class ReportsPage : RemotePage
{
    readonly Entry _query = RemoteUi.Field("Song, or leave blank");
    readonly Entry _from = RemoteUi.Field("From yyyy-MM-dd");
    readonly Entry _to = RemoteUi.Field("To yyyy-MM-dd");
    readonly Switch _skipped = new();
    readonly Label _status = RemoteUi.Muted("Song play history, music, and the event log.");
    readonly VerticalStackLayout _rows = new();
    string _mode = "songs";
    int _offset;
    string? _trackKey;

    public ReportsPage()
    {
        var modes = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
        var songs = RemoteUi.Button("Songs", () => SetMode("songs"));
        var music = RemoteUi.Button("Music", () => SetMode("music"));
        var events = RemoteUi.Button("Events", () => SetMode("events"));
        Grid.SetColumn(songs, 0);
        Grid.SetColumn(music, 1);
        Grid.SetColumn(events, 2);
        modes.Add(songs);
        modes.Add(music);
        modes.Add(events);

        Content = RemoteUi.Page(
            "Reports",
            _status,
            RemoteUi.Section(
                PhoneSections.ReportFilters,
                "Filters",
                modes,
                _query,
                _from,
                _to,
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { _skipped, new Label { Text = "Skipped only", TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center } },
                },
                RemoteUi.Button("Load", () => LoadAsync(reset: true))),
            _rows,
            RemoteUi.Button("Load older", () => LoadAsync(reset: false)));
    }

    protected override void OnShown()
    {
        base.OnShown();
        if (_rows.Children.Count == 0)
            _ = LoadAsync(reset: true);
    }

    protected override void OnLinkChanged() => _ = LoadAsync(reset: true);

    Task SetMode(string mode)
    {
        _mode = mode;
        _trackKey = null;
        return LoadAsync(reset: true);
    }

    async Task LoadAsync(bool reset)
    {
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _rows.Clear();
            _rows.Add(RemoteUi.NeedsPc("Song play history, music, and the event log"));
            _status.Text = AppServices.Link.DrivePc
                ? (AppServices.Link.Problem ?? "Cannot reach the PC.")
                : "Song play history, music, and the event log need the PC.";
            return;
        }

        if (reset)
        {
            _offset = 0;
            _rows.Clear();
        }

        if (_mode == "music")
        {
            var call = await AppServices.Client.GetMusicAsync(_from.Text, _to.Text, _skipped.IsToggled, 40, _offset, CancellationToken.None);
            var page = call.Value;
            if (page is null)
            {
                _status.Text = call.Detail ?? "Music report failed.";
                return;
            }

            if (reset)
            {
                foreach (var rank in page.MostPlayed.Take(5))
                    _rows.Add(RemoteUi.Muted($"Most played · {rank.Title} — {rank.Artist} · {rank.Count}"));
                foreach (var rank in page.MostSkipped.Take(5))
                    _rows.Add(RemoteUi.Muted($"Most skipped · {rank.Title} — {rank.Artist} · {rank.Count}"));
            }

            foreach (var play in page.Plays)
                _rows.Add(RemoteUi.Muted($"{play.WhenLocal} · {play.Title} — {play.Artist} · {play.Outcome} · {play.Listened}/{play.Length}"));
            _offset += page.Plays.Count;
            _status.Text = $"{page.Plays.Count} music rows";
            return;
        }

        if (_mode == "events")
        {
            var call = await AppServices.Client.GetEventsAsync(null, null, 40, _offset, CancellationToken.None);
            var page = call.Value;
            if (page is null)
            {
                _status.Text = call.Detail ?? "Event log failed.";
                return;
            }

            foreach (var row in page.Events)
                _rows.Add(RemoteUi.Muted($"{row.WhenLocal} · {row.Category} · {row.Kind} · {row.Summary}"));
            _offset += page.Events.Count;
            _status.Text = $"{page.Events.Count} events";
            return;
        }

        if (!string.IsNullOrWhiteSpace(_trackKey) && !reset)
        {
            await LoadPlaysAsync();
            return;
        }

        var songs = await AppServices.Client.GetSongsAsync(_query.Text, 40, _offset, CancellationToken.None);
        if (songs.Value is not { } list)
        {
            _status.Text = songs.Detail ?? "Song history failed.";
            return;
        }

        foreach (var song in list.Songs)
        {
            var key = song.TrackKey;
            _rows.Add(RemoteUi.Button(
                $"{song.Title} — {song.Artist} · {song.PlayCount} plays · {song.SkipCount} skips",
                () => OpenSongAsync(key)));
        }

        _offset += list.Songs.Count;
        _status.Text = list.Songs.Count == 0 ? "No songs." : "Tap a song for each play.";
    }

    async Task OpenSongAsync(string key)
    {
        _trackKey = key;
        _offset = 0;
        _rows.Clear();
        await LoadPlaysAsync();
    }

    async Task LoadPlaysAsync()
    {
        if (string.IsNullOrWhiteSpace(_trackKey))
            return;
        var call = await AppServices.Client.GetPlaysAsync(_trackKey, 40, _offset, CancellationToken.None);
        foreach (var play in call.Value?.Plays ?? [])
            _rows.Add(RemoteUi.Muted($"{play.WhenLocal} · {play.Zone} · {play.Outcome} · {play.Trigger} · {play.Listened}/{play.Length}"));
        _offset += call.Value?.Plays.Count ?? 0;
        _status.Text = "Plays for the selected song. Load the song list again with Load.";
    }
}
