using HotSonos.Core.Models;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class LibraryPage : RemotePage
{
    readonly Entry _query = RemoteUi.Field("Search title, artist, T:, A:, TG:, F:");
    readonly Label _status = RemoteUi.Muted("");
    readonly VerticalStackLayout _results = new() { Spacing = 2 };
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
            () => Speaker((s, ct) => s.InsertLibraryUriAndPlayNowAsync(track.Uri, track.Title, track.Artist, ct), _status),
            () => MoreAsync(track.Title, tags: false, wipe: () => WipeSpeakerAsync(track)));

    async Task WipeSpeakerAsync(SonosLibraryTrack track)
    {
        if (!await Confirm("Wipe the queue", $"Replace the queue with {track.Title}? The current queue is cleared."))
            return;
        await Speaker((s, ct) => s.PlayLibraryUriAsync(track.Uri, track.Title, track.Artist, ct), _status);
    }

    View TrackCard(RemoteTrackDto track) =>
        SongRow(
            track.Title ?? track.Path,
            Line(track.Artist, track.Album),
            () => Act(ct => AppServices.Client.PlayTrackAsync(track.Path, track.Title, track.Artist, ct), _status),
            () => MoreAsync(track.Title ?? track.Path, tags: true, wipe: () => WipeAsync(track), toggle: () => ToggleAsync(track)));

    async Task MoreAsync(string title, bool tags, Func<Task> wipe, Func<Task>? toggle = null)
    {
        var pick = await Dialogs.Sheet(this, title, "Cancel", null, [.. LibraryRowActions.Sheet(tags)]);
        if (pick == LibraryRowActions.Wipe)
            await wipe();
        else if (pick == LibraryRowActions.ToggleTag && toggle is not null)
            await toggle();
    }

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
        var dots = new Label
        {
            Text = "···",
            TextColor = Swatches.Muted,
            FontSize = 18,
            WidthRequest = 36,
            HeightRequest = LibraryRowActions.RowHeight,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        var row = new Grid
        {
            HeightRequest = LibraryRowActions.RowHeight,
            MinimumHeightRequest = LibraryRowActions.RowHeight,
            MaximumHeightRequest = LibraryRowActions.RowHeight,
            Padding = new Thickness(12, 0, 4, 0),
            ColumnSpacing = 0,
            BackgroundColor = Swatches.Card,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(dots, 1);
        row.Add(text);
        row.Add(dots);
        SemanticProperties.SetDescription(row, $"Play {title}");
        SemanticProperties.SetDescription(dots, "More");
        // Tap and long-press cannot share an Android view: Activate consumes the
        // down event, so hold never fires. The row tap plays; ··· opens the sheet.
        RemoteInput.MakeActivable(row, play);
        RemoteInput.MakeActivable(dots, more);
        return row;
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
            await Dialogs.Alert(this, "Tags", "No tags yet.");
            return;
        }

        var pick = await Dialogs.Sheet(this, "Toggle tag", "Cancel", null, labels);
        var tag = tags.Value?.Tags.FirstOrDefault(t => t.Label == pick);
        if (tag is null)
            return;
        await Act(ct => AppServices.Client.ToggleTagAsync(track.Path, tag.Key, ct), _status);
        await SearchAsync(reset: true);
    }

}
