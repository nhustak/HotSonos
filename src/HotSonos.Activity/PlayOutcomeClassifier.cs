namespace HotSonos.Activity;

/// <summary>How a play attempt finished. Stored lowercase in SQLite.</summary>
public static class PlayOutcomes
{
    public const string Completed = "completed";
    public const string Skipped = "skipped";
    public const string Stopped = "stopped";
    public const string Interrupted = "interrupted";
    public const string Unknown = "unknown";
}

/// <summary>Why a play ended, when we know. Stored lowercase in SQLite.</summary>
public static class PlayTriggers
{
    public const string HotkeyNext = "hotkey_next";
    public const string HotkeyPrevious = "hotkey_previous";
    public const string ControllerNext = "controller_next";
    public const string Stop = "stop";
    public const string NaturalEnd = "natural_end";
    public const string SpeakerLost = "speaker_lost";
    public const string ShuffleStart = "shuffle_start";
    public const string PlayNow = "play_now";
    public const string TrackChanged = "track_changed";
    public const string SeedLastPlayed = "seed_last_played";
    public const string SeedPlayEvent = "seed_play_event";
}

public static class PlayEndReasons
{
    public const string TrackChanged = "track_changed";
    public const string TransportStopped = "transport_stopped";
}

/// <summary>Facts available when a play session closes. Times are seconds.</summary>
public readonly record struct PlayEndFacts(
    double? ListenedSeconds,
    double? TrackSeconds,
    string EndReason,
    string? ExplicitTrigger);

public readonly record struct PlayClassification(string Outcome, string Trigger);

/// <summary>
/// Classifies how a song ended.
///
/// <list type="bullet">
/// <item><description>
/// <b>Skipped / hotkey_next</b> when HotSonos issued Next. That wins even if the
/// playhead was already near the end — a Next was issued.
/// </description></item>
/// <item><description>
/// <b>Interrupted / hotkey_previous</b> when HotSonos issued Previous.
/// </description></item>
/// <item><description>
/// <b>Interrupted</b> for shuffle start, play-now, or the active speaker dropping,
/// unless the playhead had already reached the end (then it is completed).
/// </description></item>
/// <item><description>
/// <b>Completed / natural_end</b> when nobody issued Next and the song reached
/// the end: at least 85% of a known duration, or (for tracks of 60s or longer)
/// inside the last 15 seconds. The 15-second tail is there because the poll is
/// 5 seconds and the last sample often lands just before Sonos advances.
/// </description></item>
/// <item><description>
/// <b>Skipped / controller_next</b> when the track changes well before that end
/// and HotSonos did not issue the command (another controller, the Sonos app,
/// or a queue replace we did not label).
/// </description></item>
/// <item><description>
/// <b>Stopped / stop</b> when transport goes to Stopped before the end and no
/// new track replaces it. Stopping in the completion window counts as completed.
/// </description></item>
/// <item><description>
/// <b>Unknown / track_changed</b> when the duration was never reported and the
/// song was heard for at least 45 seconds. Under 45 seconds with no duration
/// counts as a skip — a real play almost always lasts longer than that, and a
/// Next from another controller often does not.
/// </description></item>
/// </list>
///
/// Listened time prefers the highest playhead position (UPnP RelTime). When no
/// position was observed, it is the summed time between playing samples. Gaps
/// longer than 2 minutes are not counted (sleep, a stalled poll) unless the
/// session has no other samples, in which case a single span up to 6 hours is
/// kept so a start/end pair with no middle poll still has a duration.
/// </summary>
public static class PlayOutcomeClassifier
{
    public const double CompletionFraction = 0.85;
    public const double CompletionTailSeconds = 15;
    public const double TailAppliesAtSeconds = 60;
    public const double UnknownSkipUnderSeconds = 45;
    public const double MinimumKnownDurationSeconds = 15;

    public const string Summary =
        "Completed: heard at least 85% of the track, or the last 15 seconds of a track " +
        "that is a minute or longer, and HotSonos did not send Next. " +
        "Skipped: HotSonos Next, or the track changed well before that point " +
        "(another controller's Next when HotSonos did not issue it). " +
        "Stopped: transport stopped before the end. " +
        "Interrupted: Previous, shuffle, play-now, or the speaker dropped. " +
        "Unknown: duration was not reported and the song played at least 45 seconds.";

    public static PlayClassification Classify(PlayEndFacts facts)
    {
        var trigger = string.IsNullOrWhiteSpace(facts.ExplicitTrigger) ? null : facts.ExplicitTrigger;
        var completed = ReachedEnd(facts.ListenedSeconds, facts.TrackSeconds);

        if (trigger == PlayTriggers.HotkeyNext)
            return new(PlayOutcomes.Skipped, PlayTriggers.HotkeyNext);
        if (trigger == PlayTriggers.HotkeyPrevious)
            return new(PlayOutcomes.Interrupted, PlayTriggers.HotkeyPrevious);

        if (trigger is PlayTriggers.ShuffleStart or PlayTriggers.PlayNow or PlayTriggers.SpeakerLost)
        {
            if (completed)
                return new(PlayOutcomes.Completed, PlayTriggers.NaturalEnd);
            return new(PlayOutcomes.Interrupted, trigger);
        }

        if (facts.EndReason == PlayEndReasons.TransportStopped)
        {
            if (completed)
                return new(PlayOutcomes.Completed, PlayTriggers.NaturalEnd);
            return new(PlayOutcomes.Stopped, PlayTriggers.Stop);
        }

        if (completed)
            return new(PlayOutcomes.Completed, PlayTriggers.NaturalEnd);

        if (facts.TrackSeconds is >= MinimumKnownDurationSeconds)
            return new(PlayOutcomes.Skipped, PlayTriggers.ControllerNext);

        var heard = facts.ListenedSeconds ?? 0;
        if (heard < UnknownSkipUnderSeconds)
            return new(PlayOutcomes.Skipped, PlayTriggers.ControllerNext);

        return new(PlayOutcomes.Unknown, PlayTriggers.TrackChanged);
    }

    /// <summary>
    /// True when the playhead reached the end of a known duration.
    /// 85% always qualifies. The last 15 seconds also qualifies once the track
    /// is at least a minute, so a short song is not marked complete halfway through.
    /// </summary>
    public static bool ReachedEnd(double? listenedSeconds, double? trackSeconds)
    {
        if (listenedSeconds is not double heard || trackSeconds is not double len)
            return false;
        if (len < MinimumKnownDurationSeconds || heard < 0 || double.IsNaN(heard) || double.IsNaN(len))
            return false;
        if (heard >= len * CompletionFraction)
            return true;
        return len >= TailAppliesAtSeconds && heard >= len - CompletionTailSeconds;
    }
}
