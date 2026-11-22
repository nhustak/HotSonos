namespace HotSonos.Remote.Protocol.Tests;

public class PhoneSectionTests
{
    [Fact]
    public void FirstVisit_OpensTheListsPeopleUseAndLeavesTheRestClosed()
    {
        Assert.True(PhoneSections.DefaultOpen(PhoneSections.NowSlots));
        Assert.True(PhoneSections.DefaultOpen(PhoneSections.PlayTags));
        Assert.True(PhoneSections.DefaultOpen(PhoneSections.RoomsGroups));
        Assert.True(PhoneSections.DefaultOpen(PhoneSections.RoomsVolume));
        Assert.True(PhoneSections.DefaultOpen(PhoneSections.SpeakersDaily));
        Assert.True(PhoneSections.DefaultOpen(PhoneSections.LibraryFolders));
        Assert.True(PhoneSections.DefaultOpen(PhoneSections.PlaybackShuffle));
        Assert.True(PhoneSections.DefaultOpen(PhoneSections.Wake));

        Assert.False(PhoneSections.DefaultOpen(PhoneSections.NowFavorites));
        Assert.False(PhoneSections.DefaultOpen(PhoneSections.PlayGenres));
        Assert.False(PhoneSections.DefaultOpen(PhoneSections.PlayFolders));
        Assert.False(PhoneSections.DefaultOpen(PhoneSections.RoomsSpeakers));
        Assert.False(PhoneSections.DefaultOpen(PhoneSections.LibraryMaster));
        Assert.False(PhoneSections.DefaultOpen(PhoneSections.LibraryTags));
        Assert.False(PhoneSections.DefaultOpen(PhoneSections.Nightly));
        Assert.False(PhoneSections.DefaultOpen(PhoneSections.AboutHeard));
        Assert.False(PhoneSections.DefaultOpen("not-a-section"));
    }

    [Fact]
    public void RememberedChoice_WinsOverTheDefault()
    {
        var store = new MemorySectionStore();
        Assert.True(PhoneSections.Resolve(PhoneSections.NowSlots, store));

        store.Write(PhoneSections.NowSlots, false);
        store.Write(PhoneSections.NowFavorites, true);

        Assert.False(PhoneSections.Resolve(PhoneSections.NowSlots, store));
        Assert.True(PhoneSections.Resolve(PhoneSections.NowFavorites, store));
        Assert.False(PhoneSections.Resolve(PhoneSections.PlayFolders, store));
    }

    [Fact]
    public void Resolve_RejectsABlankKey()
    {
        Assert.Throws<ArgumentException>(() => PhoneSections.Resolve("  ", new MemorySectionStore()));
    }
}
