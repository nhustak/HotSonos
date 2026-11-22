using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class NowPlayingTagsTests
{
    [Fact]
    public void Describe_marks_only_the_keys_already_on_the_track()
    {
        var page = NowPlayingTags.Describe(
            @"\\nas\music\a.flac",
            blockedMessage: null,
            catalog: [("favs", "Favs"), ("dinner", "Dinner"), ("", "skip")],
            onKeys: ["favs"]);

        Assert.True(page.Ok);
        Assert.True(page.CanTag);
        Assert.Equal(@"\\nas\music\a.flac", page.Path);
        Assert.Null(page.Message);
        Assert.Equal(2, page.Tags.Count);
        Assert.Equal(("favs", "Favs", true), (page.Tags[0].Key, page.Tags[0].Label, page.Tags[0].On));
        Assert.False(page.Tags[1].On);
    }

    [Fact]
    public void Describe_blocks_a_track_the_library_cannot_write()
    {
        var page = NowPlayingTags.Describe(
            path: null,
            blockedMessage: "Nothing playing.",
            catalog: [("dinner", "Dinner")],
            onKeys: null);

        Assert.False(page.CanTag);
        Assert.Null(page.Path);
        Assert.Equal("Nothing playing.", page.Message);
        Assert.Equal("Dinner", page.Tags[0].Label);
        Assert.False(page.Tags[0].On);
    }
}
