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
}
