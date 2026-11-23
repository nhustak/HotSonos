using HotSonos.Core.Models;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class LibraryPage : RemotePage
{
    readonly Entry _query = RemoteUi.Field("Search title, artist, T:, A:, TG:, F:");
    readonly Label _status = RemoteUi.Muted("");
    readonly VerticalStackLayout _results = new() { Spacing = 0 };
    int _offset;

    public LibraryPage()
    {
        Content = RemoteUi.Page(
            "Library",
            _status,
            _query,
            RemoteUi.Button("Search", () => SearchAsync(reset: true)),
            _results,
            RemoteUi.Button("Load older", () => SearchAsync(reset: false)));
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = SearchAsync(reset: true);
    }

    protected override void OnLinkChanged() => _ = SearchAsync(reset: true);

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
            _status.Text = PairingPrompt.Miss(call, "Search failed.");
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

    View SpeakerCard(SonosLibraryTrack track) =>
        SongRow(
            track.Title,
            Line(track.Artist, track.Album),
            () => PlaySpeakerAsync(track, LibraryRowActions.PlayNow),
            () => MoreSpeakerAsync(track));

    async Task MoreSpeakerAsync(SonosLibraryTrack track)
    {
        var pick = await Dialogs.Menu(this, track.Title, [.. LibraryRowActions.Sheet(tags: false)]);
        if (pick is LibraryRowActions.PlayNow or LibraryRowActions.PlayNext or LibraryRowActions.AddToQueue)
            await PlaySpeakerAsync(track, pick);
    }

    Task PlaySpeakerAsync(SonosLibraryTrack track, string action) => action switch
    {
        LibraryRowActions.PlayNow => Speaker(
            (s, ct) => s.InsertLibraryUriAndPlayNowAsync(track.Uri, track.Title, track.Artist, ct),
            _status, done: $"Playing {track.Title}."),
        LibraryRowActions.PlayNext => Speaker(
            (s, ct) => s.InsertLibraryUriNextAsync(track.Uri, track.Title, track.Artist, ct),
            _status, done: $"Next: {track.Title}."),
        LibraryRowActions.AddToQueue => Speaker(
            (s, ct) => s.InsertLibraryUriAppendAsync(track.Uri, track.Title, track.Artist, ct),
            _status, done: $"Added {track.Title}."),
        _ => Task.CompletedTask,
    };

    View TrackCard(RemoteTrackDto track) =>
        SongRow(
            track.Title ?? track.Path,
            Line(track.Artist, track.Album),
            () => PlayPcAsync(track, LibraryRowActions.PlayNow),
            () => MoreTrackAsync(track));

    async Task MoreTrackAsync(RemoteTrackDto track)
    {
        var pick = await Dialogs.Menu(this, track.Title ?? track.Path, [.. LibraryRowActions.Sheet(tags: true)]);
        if (pick == LibraryRowActions.ToggleTag)
            await ToggleAsync(track);
        else if (pick is LibraryRowActions.PlayNow or LibraryRowActions.PlayNext or LibraryRowActions.AddToQueue)
            await PlayPcAsync(track, pick);
    }

    Task PlayPcAsync(RemoteTrackDto track, string action) => action switch
    {
        LibraryRowActions.PlayNow => Act(
            ct => AppServices.Client.PlayTrackAsync(track.Path, track.Title, track.Artist, ct),
            _status, done: $"Playing {track.Title ?? track.Path}."),
        LibraryRowActions.PlayNext => Act(
            ct => AppServices.Client.QueueTrackAsync(track.Path, track.Title, track.Artist, ct),
            _status, done: $"Next: {track.Title ?? track.Path}."),
        LibraryRowActions.AddToQueue => Act(
            ct => AppServices.Client.AppendTrackAsync(track.Path, track.Title, track.Artist, ct),
            _status, done: $"Added {track.Title ?? track.Path}."),
        _ => Task.CompletedTask,
    };

    static string Line(params string?[] parts) =>
        string.Join(" · ", parts.Where(static p => !string.IsNullOrWhiteSpace(p)));

    static View SongRow(string? title, string detail, Func<Task> play, Func<Task> more)
    {
        var name = new Label
        {
            Text = title ?? "",
            TextColor = Swatches.Ink,
            FontAttributes = FontAttributes.Bold,
            FontSize = 15,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            VerticalTextAlignment = TextAlignment.End,
        };
        var sub = new Label
        {
            Text = detail,
            TextColor = Swatches.Muted,
            FontSize = 12,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            VerticalTextAlignment = TextAlignment.Start,
            IsVisible = !string.IsNullOrWhiteSpace(detail),
        };
        var text = new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
            Children = { name, sub },
        };
        var menu = new Label
        {
            Text = "⋮",
            TextColor = Swatches.Muted,
            FontSize = 22,
            WidthRequest = 40,
            HeightRequest = LibraryRowActions.RowHeight,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        var row = new Grid
        {
            HeightRequest = LibraryRowActions.RowHeight,
            MinimumHeightRequest = LibraryRowActions.RowHeight,
            MaximumHeightRequest = LibraryRowActions.RowHeight,
            Padding = new Thickness(12, 0, 0, 0),
            ColumnSpacing = 0,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(menu, 1);
        row.Add(text);
        row.Add(menu);
        SemanticProperties.SetDescription(row, $"Play now: {title}");
        SemanticProperties.SetDescription(menu, "More");
        RemoteInput.MakeActivable(row, play);
        RemoteInput.MakeActivable(menu, more);

        return new VerticalStackLayout
        {
            Spacing = 0,
            Children =
            {
                row,
                new BoxView
                {
                    HeightRequest = 1,
                    Color = Color.FromArgb("#2A3340"),
                    HorizontalOptions = LayoutOptions.Fill,
                },
            },
        };
    }

    async Task ToggleAsync(RemoteTrackDto track)
    {
        var tags = await AppServices.Client.GetTagsAsync(CancellationToken.None);
        var labels = tags.Value?.Tags.Select(t => t.Label).ToArray() ?? [];
        if (labels.Length == 0)
        {
            await Dialogs.Alert(this, "Tags", "No tags yet.");
            return;
        }

        var pick = await Dialogs.Menu(this, "Toggle tag", labels);
        var tag = tags.Value?.Tags.FirstOrDefault(t => t.Label == pick);
        if (tag is null)
            return;
        await Act(ct => AppServices.Client.ToggleTagAsync(track.Path, tag.Key, ct), _status);
        await SearchAsync(reset: true);
    }

}
