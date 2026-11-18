namespace HotSonos.Activity;

public static class ActivityText
{
    public static string FormatClock(double? seconds)
    {
        if (seconds is not double s || s < 0 || double.IsNaN(s) || double.IsInfinity(s))
            return "—";
        var t = TimeSpan.FromSeconds(s);
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string OutcomeLabel(string? outcome) => outcome switch
    {
        PlayOutcomes.Completed => "Completed",
        PlayOutcomes.Skipped => "Skipped",
        PlayOutcomes.Stopped => "Stopped",
        PlayOutcomes.Interrupted => "Interrupted",
        PlayOutcomes.Unknown => "Unknown",
        _ => string.IsNullOrWhiteSpace(outcome) ? "" : outcome,
    };

    public static string TriggerLabel(string? trigger) => trigger switch
    {
        PlayTriggers.HotkeyNext => "HotSonos next",
        PlayTriggers.HotkeyPrevious => "HotSonos previous",
        PlayTriggers.ControllerNext => "Next from another controller",
        PlayTriggers.Stop => "Stop",
        PlayTriggers.NaturalEnd => "Played to the end",
        PlayTriggers.SpeakerLost => "Speaker lost",
        PlayTriggers.ShuffleStart => "Shuffle started",
        PlayTriggers.PlayNow => "Play now",
        PlayTriggers.TrackChanged => "Track changed",
        PlayTriggers.SeedLastPlayed => "Seeded from last played",
        PlayTriggers.SeedPlayEvent => "Seeded from play events",
        _ => string.IsNullOrWhiteSpace(trigger) ? "" : trigger.Replace('_', ' '),
    };

    public static string CategoryLabel(string? category) => category switch
    {
        ActivityCategories.Music => "Music",
        ActivityCategories.Sonos => "Sonos",
        _ => category ?? "",
    };

    public static string TrackLabel(string? title, string? artist, string? key)
    {
        if (!string.IsNullOrWhiteSpace(title))
            return string.IsNullOrWhiteSpace(artist) ? title.Trim() : $"{title.Trim()} — {artist.Trim()}";
        if (!string.IsNullOrWhiteSpace(key))
        {
            var line = key.Split('\n')[0];
            var slash = line.LastIndexOf('/');
            var name = slash >= 0 && slash < line.Length - 1 ? line[(slash + 1)..] : line;
            return name;
        }
        return "(unknown track)";
    }

    public static string LocalStamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
