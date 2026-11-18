namespace HotSonos.Activity.Tests;

public class ActivityLogTests : IDisposable
{
    private readonly string _dir;

    public ActivityLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hotsonos-activity-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Sessions_and_events_round_trip_through_the_reports()
    {
        using var log = Open();
        var t0 = new DateTime(2026, 11, 10, 18, 0, 0, DateTimeKind.Utc);
        log.Apply(Sample(t0, "nas/music/one.flac", "One", position: 5, duration: 200));
        log.NoteHotkeyNext();
        log.Apply(Sample(t0.AddSeconds(25), "nas/music/one.flac", "One", position: 25, duration: 200));
        log.Apply(Sample(t0.AddSeconds(26), "nas/music/two.flac", "Two", position: 1, duration: 180));
        log.NoteVolume("House volume 22%");
        log.NoteZonePresence("Kitchen", online: false, activeCoordinator: false);
        log.NoteError("Action Next failed", "timeout");

        var songs = log.SearchSongs("one");
        var one = Assert.Single(songs);
        Assert.Equal(1, one.PlayCount);
        Assert.Equal(1, one.SkipCount);
        Assert.Equal("One", one.Title);

        var plays = log.PlaysForSong(one.TrackKey);
        var play = Assert.Single(plays);
        Assert.Equal(PlayOutcomes.Skipped, play.Outcome);
        Assert.Equal(PlayTriggers.HotkeyNext, play.Trigger);
        Assert.Equal("0:25", play.Listened);
        Assert.Equal("3:20", play.Length);
        Assert.Equal("Living Room", play.Zone);

        var music = log.QueryMusic(new DateTime(2026, 11, 1), new DateTime(2026, 11, 30), skippedOnly: true);
        Assert.Contains(music.Plays, p => p.Title == "One");
        Assert.DoesNotContain(music.Plays, p => p.Title == "Two");
        Assert.Contains(music.MostSkipped, s => s.Title == "One");

        // Two is still the open session, so only the finished song is in the report.
        var allMusic = log.QueryMusic(null, null, skippedOnly: false);
        Assert.Single(allMusic.Plays);

        var events = log.QueryEvents(ActivityCategories.Sonos, kind: null);
        Assert.Contains(events, e => e.Kind == "hotkey_next");
        Assert.Contains(events, e => e.Kind == "volume");
        Assert.Contains(events, e => e.Kind == "zone_offline");
        Assert.Contains(events, e => e.Kind == "error");
        Assert.DoesNotContain(events, e => e.Category == ActivityCategories.Music);

        var musicEvents = log.QueryEvents(ActivityCategories.Music, "track_ended");
        Assert.Contains(musicEvents, e => e.Summary.Contains("One", StringComparison.Ordinal));
        Assert.Equal(["track_ended"], log.EventKinds(ActivityCategories.Music).Where(k => k == "track_ended").ToArray());
    }

    [Fact]
    public void Seed_imports_history_once_and_does_not_rewrite_it()
    {
        var historyPath = Path.Combine(_dir, "play-history.json");
        File.WriteAllText(historyPath,
            """
            {
              "Entries": [
                { "Uri": "x-file-cifs://nas/Music/Seeded.flac", "Utc": "2026-11-01T12:00:00Z", "Kind": "played" }
              ]
            }
            """);
        var eventsPath = Path.Combine(_dir, "play-events.jsonl");
        File.WriteAllText(eventsPath,
            """
            {"utc":"2026-11-02T12:00:00Z","kind":"started","uri":"x-file-cifs://nas/Music/Other.flac","title":"Other","artist":"A"}
            {"utc":"2026-11-02T12:01:00Z","kind":"stopped","uri":"x-file-cifs://nas/Music/Other.flac","title":"Other","source":"gena"}
            """);
        var before = File.ReadAllText(historyPath);

        var options = new ActivityLogOptions
        {
            DatabasePath = Path.Combine(_dir, "activity.db"),
            PlayHistoryPath = historyPath,
            PlayEventsPath = eventsPath,
            Seed = true,
        };
        using (var log = new ActivityLog(options))
        {
            var songs = log.SearchSongs("");
            Assert.Contains(songs, s => s.Title == "seeded");
            Assert.Contains(songs, s => s.Title == "Other");
            var other = log.PlaysForSong(songs.Single(s => s.Title == "Other").TrackKey);
            Assert.Equal(PlayOutcomes.Stopped, Assert.Single(other).Outcome);
        }

        Assert.Equal(before, File.ReadAllText(historyPath));

        using var again = new ActivityLog(options);
        Assert.Equal(2, again.SearchSongs("").Count);
    }

    [Fact]
    public void Retention_caps_the_log()
    {
        var options = Options();
        options = new ActivityLogOptions
        {
            DatabasePath = options.DatabasePath,
            PlayHistoryPath = options.PlayHistoryPath,
            PlayEventsPath = options.PlayEventsPath,
            Seed = false,
            RetentionDays = 30,
            MaxEvents = 100,
            MaxSessions = 100,
        };

        // The store caps inside ActivityLog via options. Use a tiny cap through a dedicated store
        // so this test does not depend on the 40-write prune cadence of the facade.
        using (var store = new ActivityStore(options.DatabasePath!, retentionDays: 30, maxEvents: 5, maxSessions: 5))
        {
            var t0 = DateTime.UtcNow;
            for (var i = 0; i < 8; i++)
            {
                store.InsertEvent(new ActivityEventDraft
                {
                    Utc = t0.AddMinutes(i),
                    Category = ActivityCategories.Sonos,
                    Kind = "volume",
                    Summary = "v" + i,
                });
            }

            Assert.Equal(5, store.CountEvents());
            var rows = store.QueryEvents(null, null, 20);
            Assert.Equal(5, rows.Count);
            Assert.Contains(rows, r => r.Summary == "v7");
            Assert.DoesNotContain(rows, r => r.Summary == "v0");
        }
    }

    [Fact]
    public void Line_in_is_left_out_of_the_music_report()
    {
        using var log = Open();
        var t0 = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        log.Apply(new PlaySnapshot
        {
            Utc = t0,
            TrackKey = "line-in",
            Title = "TV",
            Source = "linein",
            IsPlaying = true,
            Zone = "Living Room",
        });
        log.Apply(Sample(t0.AddMinutes(1), "nas/music/song.flac", "Real Song", position: 10, duration: 200));
        log.Apply(new PlaySnapshot
        {
            Utc = t0.AddMinutes(1).AddSeconds(20),
            TrackKey = "nas/music/song.flac",
            Title = "Real Song",
            Source = "library",
            Zone = "Living Room",
            IsStopped = true,
            PositionSeconds = 20,
            DurationSeconds = 200,
        });
        var music = log.QueryMusic(null, null, skippedOnly: false);
        Assert.DoesNotContain(music.Plays, p => p.Title == "TV");
        Assert.Contains(music.Plays, p => p.Title == "Real Song");
    }

    private ActivityLog Open() => new(Options());

    private ActivityLogOptions Options() => new()
    {
        DatabasePath = Path.Combine(_dir, "activity.db"),
        PlayHistoryPath = Path.Combine(_dir, "missing-history.json"),
        PlayEventsPath = Path.Combine(_dir, "missing-events.jsonl"),
        Seed = false,
    };

    private static PlaySnapshot Sample(
        DateTime utc, string key, string title, double? position = null, double? duration = null) => new()
    {
        Utc = utc,
        TrackKey = key,
        Uri = "x-file-cifs://" + key,
        Title = title,
        Artist = "Artist",
        Zone = "Living Room",
        Source = "library",
        IsPlaying = true,
        PositionSeconds = position,
        DurationSeconds = duration,
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            /* sqlite may still be releasing the file on this platform */
        }
    }
}
