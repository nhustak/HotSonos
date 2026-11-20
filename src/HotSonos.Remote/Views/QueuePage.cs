using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class QueuePage : RemotePage
{
    readonly Label _status = RemoteUi.Muted("");
    readonly VerticalStackLayout _queue = new();

    public QueuePage()
    {
        Content = RemoteUi.Page(
            "Queue",
            _status,
            RemoteUi.Button("Refresh", LoadAsync),
            _queue);
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    async Task LoadAsync()
    {
        _queue.Clear();
        if (!AppServices.Link.DrivePc)
        {
            await LoadSpeakerQueueAsync();
            return;
        }

        if (!AppServices.Link.OnPc)
        {
            _queue.Add(RemoteUi.NeedsPc("The queue"));
            _status.Text = AppServices.Link.Problem ?? "Cannot reach the PC.";
            return;
        }

        var queue = await AppServices.Client.GetQueueAsync(CancellationToken.None);
        var items = queue.Value?.Tracks ?? [];
        if (items.Count == 0)
            _queue.Add(RemoteUi.Muted(queue.Detail ?? "Queue is empty."));
        foreach (var track in items)
            _queue.Add(RemoteUi.Muted($"{track.Number}. {track.Title} — {track.Artist}"));
        _status.Text = items.Count == 0 ? "" : $"{items.Count} in the queue";
    }

    async Task LoadSpeakerQueueAsync()
    {
        var speaker = AppServices.Link.Speakers.Controller;
        if (speaker is null)
        {
            _status.Text = AppServices.Link.Problem ?? "No speakers yet.";
            return;
        }

        try
        {
            var queue = await speaker.GetQueueAsync(30, CancellationToken.None);
            if (queue.Count == 0)
                _queue.Add(RemoteUi.Muted("Queue is empty."));
            foreach (var track in queue)
            {
                var number = track.Number;
                _queue.Add(RemoteUi.Button($"{number}. {track.Title} — {track.Artist}", () =>
                    Speaker((s, ct) => s.SeekToTrackAsync(number, ct), _status)));
            }

            _status.Text = "Tap a track to play it from there.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }
}
