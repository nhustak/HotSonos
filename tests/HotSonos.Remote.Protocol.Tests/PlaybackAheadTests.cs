using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class PlaybackAheadTests
{
    [Fact]
    public void Next_track_is_the_queue_row_after_the_playing_one()
    {
        var tracks = new[]
        {
            new RemoteQueueItemDto(1, "Afternoon", "Ada"),
            new RemoteQueueItemDto(2, "Night", "Bea"),
            new RemoteQueueItemDto(4, "Later", "Cy"),
        };

        var next = QueueCursor.NextAfter(1, tracks);
        Assert.Equal("Night", next?.Title);
        Assert.Equal("Bea", next?.Artist);
        Assert.Null(QueueCursor.NextAfter(2, tracks));
        Assert.Null(QueueCursor.NextAfter(null, tracks));
        Assert.Null(QueueCursor.NextAfter(1, null));
    }

    [Fact]
    public void Next_press_shows_the_upcoming_title_until_the_poll_moves_on()
    {
        var face = SkipAhead.OnNext(new RemoteUpcomingDto("Night", "Bea"));
        Assert.Equal("Night", face?.Title);
        Assert.Equal("Bea", face?.Artist);
        Assert.Null(SkipAhead.OnNext(null));
        Assert.Null(SkipAhead.OnNext(new RemoteUpcomingDto("  ", "Bea")));

        Assert.True(SkipAhead.IsStale("Afternoon", "Afternoon", 0));
        Assert.True(SkipAhead.IsStale("Afternoon", " Afternoon ", SkipAhead.HoldMs - 1));
        Assert.True(SkipAhead.IsStale("Afternoon", "Nothing playing", 10));
        Assert.True(SkipAhead.IsStale("Afternoon", "  ", 10));
        Assert.False(SkipAhead.IsStale("Afternoon", "Night", 0));
        Assert.False(SkipAhead.IsStale("Afternoon", "Afternoon", SkipAhead.HoldMs));
        Assert.False(SkipAhead.IsStale(null, "Afternoon", 0));
    }

    [Fact]
    public void Confirm_is_shown_on_the_window_page()
    {
        var source = Source("src", "HotSonos.Remote", "Views", "RemoteUi.cs");
        var confirm = Between(source, "protected Task<bool> Confirm", "protected async Task<bool> Act");
        Assert.Contains("Dialogs.Confirm", confirm, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayAlert(", confirm, StringComparison.Ordinal);

        var topology = Source("src", "HotSonos.Remote", "Views", "TopologyPage.cs");
        Assert.Contains("Dialogs.Sheet", topology, StringComparison.Ordinal);
        Assert.Contains("AppServices.Client.RegroupAsync", topology, StringComparison.Ordinal);
        Assert.Contains("SetCoordinatorAsync(room, wholeHouse: true", topology, StringComparison.Ordinal);
    }

    static string Between(string source, string start, string end)
    {
        var at = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, start);
        var to = source.IndexOf(end, at, StringComparison.Ordinal);
        Assert.True(to > at, end);
        return source[at..to];
    }

    static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new InvalidOperationException("Could not find " + Path.Combine(parts));
    }
}
