using System.Xml.Linq;
using HotSonos.Core;

namespace HotSonos.Core.Tests;

public class ShuffleExclusionOrderTests
{
    private static (string Uri, XElement Item) Track(string name) =>
        ($"x-file-cifs://host/Music/Sonos/{name}.flac", new XElement("item"));

    private static HashSet<string> Keys(params string[] uris) =>
        uris.Select(SonosController.NormalizeTrackKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Unheard_pool_is_used_before_any_history()
    {
        var a = Track("a");
        var b = Track("b");
        var heard = Track("heard");
        var playedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            [SonosController.NormalizeTrackKey(heard.Uri)] = DateTime.UtcNow.AddHours(-3),
        };

        var (ordered, excluded, candidates, slid) = SonosController.BuildExclusionOrder(
            [a, b, heard],
            Keys(heard.Uri),
            playedAt,
            TimeSpan.FromHours(24),
            maxQueue: 2,
            artistSpread: false);

        Assert.Equal(2, ordered.Count);
        Assert.DoesNotContain(ordered, t => t.Uri == heard.Uri);
        Assert.Equal(1, excluded);
        Assert.Equal(2, candidates);
        Assert.Equal(0, slid);
    }

    [Fact]
    public void Short_unheard_pool_slides_oldest_outside_24h_not_recent()
    {
        var fresh = Track("fresh");
        var yesterday = Track("yesterday");
        var hourAgo = Track("hour");
        var now = DateTime.UtcNow;
        var playedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            [SonosController.NormalizeTrackKey(yesterday.Uri)] = now.AddHours(-30),
            [SonosController.NormalizeTrackKey(hourAgo.Uri)] = now.AddHours(-1),
        };

        var (ordered, _, _, slid) = SonosController.BuildExclusionOrder(
            [fresh, yesterday, hourAgo],
            Keys(yesterday.Uri, hourAgo.Uri),
            playedAt,
            TimeSpan.FromHours(24),
            maxQueue: 2,
            artistSpread: false);

        Assert.Equal(2, ordered.Count);
        Assert.Contains(ordered, t => t.Uri == fresh.Uri);
        Assert.Contains(ordered, t => t.Uri == yesterday.Uri);
        Assert.DoesNotContain(ordered, t => t.Uri == hourAgo.Uri);
        Assert.Equal(1, slid);
    }

    [Fact]
    public void All_in_scope_heard_in_last_24h_reuses_pool()
    {
        var a = Track("a");
        var b = Track("b");
        var now = DateTime.UtcNow;
        var playedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            [SonosController.NormalizeTrackKey(a.Uri)] = now.AddHours(-1),
            [SonosController.NormalizeTrackKey(b.Uri)] = now.AddHours(-2),
        };

        var (ordered, excluded, _, slid) = SonosController.BuildExclusionOrder(
            [a, b],
            Keys(a.Uri, b.Uri),
            playedAt,
            TimeSpan.FromHours(24),
            maxQueue: 2,
            artistSpread: false);

        Assert.Equal(2, ordered.Count);
        Assert.Equal(0, excluded);
        Assert.Equal(0, slid);
    }

    [Fact]
    public void Exhausted_fallback_excludes_queued()
    {
        var oldest = Track("oldest");
        var newer = Track("newer");
        var waiting = Track("waiting");
        var now = DateTime.UtcNow;
        var playedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            [SonosController.NormalizeTrackKey(oldest.Uri)] = now.AddHours(-3),
            [SonosController.NormalizeTrackKey(newer.Uri)] = now.AddHours(-1),
            // Heard earlier today and still sitting on the queue.
            [SonosController.NormalizeTrackKey(waiting.Uri)] = now.AddHours(-2),
        };

        var (ordered, _, _, _) = SonosController.BuildExclusionOrder(
            [newer, waiting, oldest],
            Keys(oldest.Uri, newer.Uri, waiting.Uri),
            playedAt,
            TimeSpan.FromHours(24),
            maxQueue: 10,
            artistSpread: true,
            queuedKeys: Keys(waiting.Uri));

        Assert.Equal([oldest.Uri, newer.Uri], ordered.Select(t => t.Uri).ToArray());

        var ancient = Track("ancient");
        var fresh = Track("fresh");
        var ancientPlayed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            [SonosController.NormalizeTrackKey(ancient.Uri)] = now.AddHours(-30),
        };
        var (shortPool, _, _, slid) = SonosController.BuildExclusionOrder(
            [fresh, ancient],
            Keys(ancient.Uri),
            ancientPlayed,
            TimeSpan.FromHours(24),
            maxQueue: 2,
            artistSpread: false,
            queuedKeys: Keys(ancient.Uri));
        Assert.Equal([fresh.Uri], shortPool.Select(t => t.Uri).ToArray());
        Assert.Equal(0, slid);

        var (none, _, _, _) = SonosController.BuildExclusionOrder(
            [waiting, oldest],
            Keys(waiting.Uri, oldest.Uri),
            playedAt,
            TimeSpan.FromHours(24),
            maxQueue: 10,
            artistSpread: false,
            queuedKeys: Keys(waiting.Uri, oldest.Uri));
        Assert.Empty(none);
    }

    [Fact]
    public void Exclude_played_off_still_excludes_queued()
    {
        var heard = Track("heard");
        var waiting = Track("waiting");
        var fresh = Track("fresh");
        var now = DateTime.UtcNow;
        var enqueuedAt = now.AddMinutes(-30);
        var heardKey = SonosController.NormalizeTrackKey(heard.Uri);
        var waitingKey = SonosController.NormalizeTrackKey(waiting.Uri);

        var plan = ShuffleExclusion.Build(
            excludePlayed: false,
            topUp: true,
            lastPlayedUtcByKey: new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
            {
                [heardKey] = enqueuedAt.AddMinutes(5),
            },
            sessionServedAtUtc: new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
            {
                [heardKey] = enqueuedAt,
                [waitingKey] = enqueuedAt,
            },
            utcNow: now);

        Assert.Null(plan.ExcludeUris);
        Assert.Null(plan.PlayedAtUtc);
        Assert.NotNull(plan.QueuedUris);
        Assert.Contains(waitingKey, plan.QueuedUris);
        Assert.DoesNotContain(heardKey, plan.QueuedUris!);

        var queued = plan.QueuedUris!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (ordered, _, _, _) = SonosController.BuildExclusionOrder(
            [heard, waiting, fresh],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(),
            TimeSpan.FromHours(24),
            maxQueue: 60,
            artistSpread: false,
            queuedKeys: queued);

        Assert.DoesNotContain(ordered, t => t.Uri == waiting.Uri);
        Assert.Contains(ordered, t => t.Uri == heard.Uri);
        Assert.Contains(ordered, t => t.Uri == fresh.Uri);
        Assert.Equal(ordered.Count, ordered.Select(t => t.Uri).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Small_scope_repro_yields_no_duplicates()
    {
        // Folder of 50, queue cap 80, top-up 60 when 4 remain. The first batch
        // takes the whole folder. Four of those are still waiting; the other 46
        // have played since enqueue. Those four also have an earlier play today,
        // which used to fall through the empty-candidate fallback and append
        // the folder again.
        var tracks = Enumerable.Range(0, 50).Select(i => Track($"t{i:00}")).ToList();
        var (first, _, _, _) = SonosController.BuildExclusionOrder(
            tracks,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(),
            TimeSpan.FromHours(24),
            maxQueue: 80,
            artistSpread: false);
        Assert.Equal(50, first.Count);
        Assert.Equal(50, first.Select(t => t.Uri).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var now = DateTime.UtcNow;
        var enqueuedAt = now.AddHours(-2);
        var played = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var served = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tracks.Count; i++)
        {
            var key = SonosController.NormalizeTrackKey(tracks[i].Uri);
            served[key] = enqueuedAt;
            played[key] = i < 46
                ? enqueuedAt.AddMinutes(i + 1)
                : now.AddHours(-5);
        }

        var plan = ShuffleExclusion.Build(
            excludePlayed: true,
            topUp: true,
            played,
            served,
            now);

        var exclude = plan.ExcludeUris!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queued = plan.QueuedUris!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4, queued.Count);
        Assert.Equal(50, exclude.Count);

        var (topUp, _, _, _) = SonosController.BuildExclusionOrder(
            tracks,
            exclude,
            plan.PlayedAtUtc!,
            TimeSpan.FromHours(24),
            maxQueue: 60,
            artistSpread: true,
            queuedKeys: queued);

        var topUris = topUp.Select(t => t.Uri).ToArray();
        var waitingUris = tracks.Skip(46).Select(t => t.Uri).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(46, topUris.Length);
        Assert.Equal(46, topUris.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(topUris.Intersect(waitingUris, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(tracks.Take(46).Select(t => t.Uri).ToArray(), topUris);
    }

    [Fact]
    public void Initial_shuffle_stamps_session_served_and_does_not_pass_queued()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        const string heardKey = "host/music/heard.flac";
        const string servedKey = "host/music/served.flac";
        var plan = ShuffleExclusion.Build(
            excludePlayed: true,
            topUp: false,
            lastPlayedUtcByKey: new Dictionary<string, DateTime>
            {
                [heardKey] = now.AddDays(-2),
            },
            sessionServedAtUtc: new Dictionary<string, DateTime>
            {
                [servedKey] = now.AddHours(-1),
            },
            utcNow: now);

        Assert.Null(plan.QueuedUris);
        Assert.Equal(now.AddDays(-2), plan.PlayedAtUtc![heardKey]);
        Assert.Equal(now, plan.PlayedAtUtc[servedKey]);
        Assert.Contains(heardKey, plan.ExcludeUris!);
        Assert.Contains(servedKey, plan.ExcludeUris!);
    }
}
