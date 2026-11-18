namespace HotSonos.Activity.Tests;

public class PlayOutcomeClassifierTests
{
    private static PlayEndFacts Facts(
        double? heard,
        double? length,
        string reason = PlayEndReasons.TrackChanged,
        string? trigger = null) =>
        new(heard, length, reason, trigger);

    [Fact]
    public void Hotkey_next_is_always_a_skip()
    {
        var nearEnd = PlayOutcomeClassifier.Classify(Facts(200, 210, trigger: PlayTriggers.HotkeyNext));
        Assert.Equal(PlayOutcomes.Skipped, nearEnd.Outcome);
        Assert.Equal(PlayTriggers.HotkeyNext, nearEnd.Trigger);
    }

    [Fact]
    public void Hotkey_previous_interrupts()
    {
        var result = PlayOutcomeClassifier.Classify(Facts(40, 200, trigger: PlayTriggers.HotkeyPrevious));
        Assert.Equal(PlayOutcomes.Interrupted, result.Outcome);
        Assert.Equal(PlayTriggers.HotkeyPrevious, result.Trigger);
    }

    [Fact]
    public void Natural_advance_near_the_end_is_completed()
    {
        // 3:10 of 3:30 is inside the last 15 seconds of a track longer than a minute.
        var tail = PlayOutcomeClassifier.Classify(Facts(195, 210));
        Assert.Equal(PlayOutcomes.Completed, tail.Outcome);
        Assert.Equal(PlayTriggers.NaturalEnd, tail.Trigger);

        // 85% of a 30s track (the 15s tail must not mark the halfway point complete).
        var fraction = PlayOutcomeClassifier.Classify(Facts(26, 30));
        Assert.Equal(PlayOutcomes.Completed, fraction.Outcome);
        Assert.False(PlayOutcomeClassifier.ReachedEnd(15, 30));
    }

    [Fact]
    public void Early_track_change_without_a_hotkey_is_another_controllers_next()
    {
        var result = PlayOutcomeClassifier.Classify(Facts(20, 200));
        Assert.Equal(PlayOutcomes.Skipped, result.Outcome);
        Assert.Equal(PlayTriggers.ControllerNext, result.Trigger);
    }

    [Fact]
    public void Stop_before_the_end_is_stopped_and_stop_at_the_end_is_completed()
    {
        var early = PlayOutcomeClassifier.Classify(
            Facts(30, 200, PlayEndReasons.TransportStopped));
        Assert.Equal(PlayOutcomes.Stopped, early.Outcome);
        Assert.Equal(PlayTriggers.Stop, early.Trigger);

        var done = PlayOutcomeClassifier.Classify(
            Facts(190, 200, PlayEndReasons.TransportStopped));
        Assert.Equal(PlayOutcomes.Completed, done.Outcome);
    }

    [Fact]
    public void Shuffle_and_speaker_loss_interrupt_unless_the_song_already_finished()
    {
        var cut = PlayOutcomeClassifier.Classify(Facts(40, 200, trigger: PlayTriggers.ShuffleStart));
        Assert.Equal(PlayOutcomes.Interrupted, cut.Outcome);
        Assert.Equal(PlayTriggers.ShuffleStart, cut.Trigger);

        var dropped = PlayOutcomeClassifier.Classify(Facts(10, 180, trigger: PlayTriggers.SpeakerLost));
        Assert.Equal(PlayOutcomes.Interrupted, dropped.Outcome);

        var almost = PlayOutcomeClassifier.Classify(Facts(200, 210, trigger: PlayTriggers.PlayNow));
        Assert.Equal(PlayOutcomes.Completed, almost.Outcome);
    }

    [Fact]
    public void Unknown_duration_uses_a_45_second_line()
    {
        var quick = PlayOutcomeClassifier.Classify(Facts(12, null));
        Assert.Equal(PlayOutcomes.Skipped, quick.Outcome);
        Assert.Equal(PlayTriggers.ControllerNext, quick.Trigger);

        var longer = PlayOutcomeClassifier.Classify(Facts(90, null));
        Assert.Equal(PlayOutcomes.Unknown, longer.Outcome);
        Assert.Equal(PlayTriggers.TrackChanged, longer.Trigger);
    }
}
