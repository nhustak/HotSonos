namespace HotSonos.Activity.Tests;

public class ActivitySeedTests
{
    [Fact]
    public void Play_events_pair_and_history_fills_only_missing_songs()
    {
        var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var events = new[]
        {
            new SeedPlayEvent
            {
                Utc = t0,
                Kind = "started",
                Uri = "x-file-cifs://nas/Music/a.flac",
                Title = "Alpha",
                Artist = "One",
            },
            new SeedPlayEvent
            {
                Utc = t0.AddMinutes(1),
                Kind = "skipped",
                Uri = "x-file-cifs://nas/Music/a.flac",
                Title = "Alpha",
                Source = "hotkey",
            },
            new SeedPlayEvent
            {
                Utc = t0.AddMinutes(2),
                Kind = "started",
                Uri = "x-file-cifs://nas/Music/b.flac",
                Title = "Beta",
            },
        };
        var history = new[]
        {
            new SeedHistoryEntry { Uri = "x-file-cifs://nas/Music/a.flac", Utc = t0, Kind = "played" },
            new SeedHistoryEntry { Uri = "x-file-cifs://nas/Music/c.flac", Utc = t0.AddHours(1), Kind = "played" },
        };

        var sessions = ActivitySeed.Build(history, events);

        Assert.Equal(2, sessions.Count);
        var alpha = sessions.Single(s => s.Title == "Alpha");
        Assert.Equal(PlayOutcomes.Skipped, alpha.Outcome);
        Assert.Equal(PlayTriggers.HotkeyNext, alpha.Trigger);
        Assert.Equal(TrackKeys.Normalize("x-file-cifs://nas/Music/a.flac"), alpha.TrackKey);

        var seeded = sessions.Single(s => s.Trigger == PlayTriggers.SeedLastPlayed);
        Assert.Equal("c", seeded.Title);
        Assert.Equal(PlayOutcomes.Unknown, seeded.Outcome);
        Assert.DoesNotContain(sessions, s => s.Title == "Beta");
    }

    [Fact]
    public void Normalize_matches_the_shuffle_key_shape()
    {
        Assert.Equal(
            "nas/music/blue in green.flac",
            TrackKeys.Normalize(@"x-file-cifs://NAS/Music/Blue%20in%20Green.flac?extra=1"));
    }
}
