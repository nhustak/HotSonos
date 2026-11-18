namespace HotSonos.Activity.Tests;

public class PlaySessionTrackerTests
{
    private static PlaySnapshot Snap(
        DateTime utc,
        string key,
        bool playing = true,
        bool paused = false,
        bool stopped = false,
        double? position = null,
        double? duration = null,
        string? title = null,
        string? zone = "Kitchen") => new()
    {
        Utc = utc,
        TrackKey = key,
        Title = title ?? key,
        Artist = "Artist",
        Zone = zone,
        Source = "library",
        IsPlaying = playing,
        IsPaused = paused,
        IsStopped = stopped,
        PositionSeconds = position,
        DurationSeconds = duration,
    };

    [Fact]
    public void Hotkey_next_closes_the_open_song_as_skipped_and_not_the_next_one()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 1, duration: 200, title: "Song A"));
        t.Apply(Snap(t0.AddSeconds(30), "song-a", position: 30, duration: 200, title: "Song A"));
        t.Arm(PlayTriggers.HotkeyNext, t0.AddSeconds(31), TimeSpan.FromSeconds(20));

        var step = t.Apply(Snap(t0.AddSeconds(32), "song-b", position: 1, duration: 180, title: "Song B"));

        var closed = Assert.Single(step.Closed);
        Assert.Equal("song-a", closed.TrackKey);
        Assert.Equal(PlayOutcomes.Skipped, closed.Outcome);
        Assert.Equal(PlayTriggers.HotkeyNext, closed.Trigger);
        Assert.Equal(30, closed.ListenedSeconds);
        Assert.Equal(200, closed.TrackSeconds);
        Assert.Equal("Kitchen", closed.Zone);
        Assert.Contains(step.Events, e => e.Kind == "track_ended" && e.Category == ActivityCategories.Music);
        Assert.Contains(step.Events, e => e.Kind == "track_started" && e.TrackKey == "song-b");

        var endB = t.Apply(Snap(t0.AddMinutes(4), "song-c", position: 1, duration: 200, title: "Song C"));
        var b = Assert.Single(endB.Closed);
        Assert.Equal("song-b", b.TrackKey);
        Assert.NotEqual(PlayTriggers.HotkeyNext, b.Trigger);
    }

    [Fact]
    public void Playhead_near_the_end_without_a_command_is_completed()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 5, duration: 210));
        t.Apply(Snap(t0.AddMinutes(3), "song-a", position: 200, duration: 210));
        var step = t.Apply(Snap(t0.AddMinutes(3).AddSeconds(5), "song-b", position: 1, duration: 200));
        var closed = Assert.Single(step.Closed);
        Assert.Equal(PlayOutcomes.Completed, closed.Outcome);
        Assert.Equal(PlayTriggers.NaturalEnd, closed.Trigger);
        Assert.Equal(200, closed.ListenedSeconds);
    }

    [Fact]
    public void Pause_does_not_end_the_session_or_count_as_listened_time()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 10, duration: 240));
        var paused = t.Apply(Snap(t0.AddSeconds(20), "song-a", playing: false, paused: true, position: 30, duration: 240));
        Assert.Empty(paused.Closed);
        Assert.Contains(paused.Events, e => e.Kind == "transport_paused" && e.Category == ActivityCategories.Sonos);

        var resumed = t.Apply(Snap(t0.AddMinutes(10), "song-a", position: 30, duration: 240));
        Assert.Empty(resumed.Closed);
        Assert.Contains(resumed.Events, e => e.Kind == "transport_resumed");

        var step = t.Apply(Snap(t0.AddMinutes(10).AddSeconds(15), "song-b", position: 1, duration: 200));
        var closed = Assert.Single(step.Closed);
        Assert.Equal(30, closed.ListenedSeconds);
        Assert.Equal(PlayOutcomes.Skipped, closed.Outcome);
    }

    [Fact]
    public void Stop_closes_as_stopped()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 12, duration: 200));
        var step = t.Apply(Snap(t0.AddSeconds(40), "song-a", playing: false, stopped: true, position: 40, duration: 200));
        var closed = Assert.Single(step.Closed);
        Assert.Equal(PlayOutcomes.Stopped, closed.Outcome);
        Assert.Equal(PlayTriggers.Stop, closed.Trigger);
        Assert.Contains(step.Events, e => e.Kind == "transport_stopped");
        Assert.False(t.HasOpenSession);
    }

    [Fact]
    public void Previous_rewind_closes_the_first_pass()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 5, duration: 200));
        t.Apply(Snap(t0.AddSeconds(40), "song-a", position: 45, duration: 200));
        t.Arm(PlayTriggers.HotkeyPrevious, t0.AddSeconds(41), TimeSpan.FromSeconds(20));
        var step = t.Apply(Snap(t0.AddSeconds(42), "song-a", position: 1, duration: 200));
        var closed = Assert.Single(step.Closed);
        Assert.Equal(PlayOutcomes.Interrupted, closed.Outcome);
        Assert.Equal(PlayTriggers.HotkeyPrevious, closed.Trigger);
        Assert.True(t.HasOpenSession);
    }

    [Fact]
    public void Sparse_start_and_end_still_records_listened_time_without_a_playhead()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", duration: 200));
        var step = t.Apply(Snap(t0.AddMinutes(3), "song-b", duration: 180));
        var closed = Assert.Single(step.Closed);
        Assert.InRange(closed.ListenedSeconds ?? 0, 179, 181);
        Assert.Equal(PlayOutcomes.Completed, closed.Outcome);
    }

    [Fact]
    public void Speaker_loss_closes_immediately()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 4, 13, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 20, duration: 200, zone: "Living Room"));
        var step = t.ForceClose(t0.AddSeconds(25), PlayTriggers.SpeakerLost);
        var closed = Assert.Single(step.Closed);
        Assert.Equal(PlayOutcomes.Interrupted, closed.Outcome);
        Assert.Equal(PlayTriggers.SpeakerLost, closed.Trigger);
        Assert.False(t.HasOpenSession);
    }

    [Fact]
    public void Expired_hotkey_does_not_stick_to_a_later_song()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 10, duration: 200));
        t.Arm(PlayTriggers.HotkeyNext, t0, TimeSpan.FromSeconds(20));
        var step = t.Apply(Snap(t0.AddMinutes(2), "song-b", position: 1, duration: 200));
        var closed = Assert.Single(step.Closed);
        Assert.NotEqual(PlayTriggers.HotkeyNext, closed.Trigger);
    }

    [Fact]
    public void A_stale_snapshot_does_not_replace_the_open_song()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 10, duration: 200));
        t.Apply(Snap(t0.AddSeconds(20), "song-b", position: 2, duration: 180));
        var stale = t.Apply(Snap(t0.AddSeconds(5), "song-a", position: 15, duration: 200));
        Assert.Empty(stale.Closed);
        Assert.Equal("song-b", t.OpenTrackKey);
    }

    [Fact]
    public void A_stream_title_arriving_later_stays_one_session()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 6, 13, 0, 0, DateTimeKind.Utc);
        var bare = TrackKeys.ForPlay("x-sonosapi-stream:station", null, titleIsIdentity: true);
        var titled = TrackKeys.ForPlay("x-sonosapi-stream:station", "Morning Show", titleIsIdentity: true);
        t.Apply(Snap(t0, bare, position: 5, duration: 0));
        var step = t.Apply(Snap(t0.AddSeconds(30), titled, position: 35, title: "Morning Show"));
        Assert.Empty(step.Closed);
        Assert.Equal(titled, t.OpenTrackKey);
    }

    [Fact]
    public void Stop_without_a_track_key_still_records_stopped()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 6, 14, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 12, duration: 200, title: "Song A"));
        var step = t.Apply(new PlaySnapshot
        {
            Utc = t0.AddSeconds(20),
            TrackKey = "",
            IsStopped = true,
            Zone = "Kitchen",
        });
        Assert.Contains(step.Events, e => e.Kind == "transport_stopped");
        Assert.Equal(PlayOutcomes.Stopped, Assert.Single(step.Closed).Outcome);
    }

    [Fact]
    public void Checkpoint_keeps_a_play_that_was_still_open()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 6, 15, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 40, duration: 200));
        var step = t.Checkpoint(t0.AddSeconds(50));
        var closed = Assert.Single(step.Closed);
        Assert.Equal(PlayTriggers.AppExit, closed.Trigger);
        Assert.Equal(PlayOutcomes.Interrupted, closed.Outcome);
        Assert.False(t.HasOpenSession);
    }

    [Fact]
    public void Disarm_drops_a_trigger_the_command_did_not_complete()
    {
        var t = new PlaySessionTracker();
        var t0 = new DateTime(2026, 11, 6, 16, 0, 0, DateTimeKind.Utc);
        t.Apply(Snap(t0, "song-a", position: 10, duration: 200));
        t.Arm(PlayTriggers.HotkeyNext, t0.AddSeconds(1), TimeSpan.FromSeconds(20));
        t.Disarm(PlayTriggers.HotkeyNext);
        var step = t.Apply(Snap(t0.AddSeconds(5), "song-b", position: 1, duration: 180));
        Assert.NotEqual(PlayTriggers.HotkeyNext, Assert.Single(step.Closed).Trigger);
    }
}
