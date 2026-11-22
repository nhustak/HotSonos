using HotSonos.Core.Models;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class LibraryPage : RemotePage
{
    readonly Entry _query = RemoteUi.Field("Search title, artist, T:, A:, TG:, F:");
    readonly Label _status = RemoteUi.Muted("");
    readonly VerticalStackLayout _results = new();
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
        var play = RemoteUi.Button(LibraryRowActions.Play, () => Speaker((s, ct) => s.InsertLibraryUriAndPlayNowAsync(track.Uri, track.Title, track.Artist, ct), _status));
        var wipe = RemoteUi.Button(LibraryRowActions.Wipe, () => WipeSpeakerAsync(track), danger: true);
        var row = ActionRow(play, wipe);
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
        var play = RemoteUi.Button(LibraryRowActions.Play, () => Act(ct => AppServices.Client.PlayTrackAsync(track.Path, track.Title, track.Artist, ct), _status));
        var wipe = RemoteUi.Button(LibraryRowActions.Wipe, () => WipeAsync(track), danger: true);
        var row = ActionRow(play, wipe);
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

    static Grid ActionRow(View play, View wipe)
    {
        var row = new Grid
        {
            ColumnSpacing = 6,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() },
        };
        Grid.SetColumn(play, 0);
        Grid.SetColumn(wipe, 1);
        row.Add(play);
        row.Add(wipe);
        return row;
    }
}
