namespace HotSonos.Core.Tests;

public class LiveGroupingTests
{
    [Fact]
    public void Map_IssueSnapshot_NamesTheRealGroupAndCountsTheRest()
    {
        var live = LiveGrouping.Map(
        [
            Member("Office", "office", coordinator: true),
            Member("Master Bath", "office", coordinator: false),
            Member("Kitchen", "kitchen", coordinator: true),
            Member("Workshop", "workshop", coordinator: true),
            Member("Living Room", "living", coordinator: true),
            Member("Sun Room", "sun", coordinator: true),
            Member("Garage", "garage", coordinator: true),
            Member("Hobby", "hobby", coordinator: true),
            Member("Theater", "theater", coordinator: true),
            Member("Game Room", "game", coordinator: true),
        ]);

        Assert.Equal("Office + Master Bath; 8 rooms ungrouped", live.Summary);
        Assert.DoesNotContain("All speakers", live.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Daily", live.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(9, live.Groups.Count);
        Assert.Equal(["Office", "Master Bath"], live.Groups[0].Rooms);
        Assert.False(LiveGrouping.MatchesDaily(live, allSpeakers: true, dailyRooms: null));
        Assert.True(LiveGrouping.MatchesDaily(live, allSpeakers: false, dailyRooms: ["Master Bath", "Office"]));
    }

    [Fact]
    public void Map_OneGroupOfEveryRoom_SaysAllSpeakers()
    {
        var live = LiveGrouping.Map(
        [
            Member("Office", "office", coordinator: true),
            Member("Kitchen", "office", coordinator: false),
            Member("Garage", "OFFICE", coordinator: false),
        ]);

        Assert.Equal(LiveGrouping.AllSpeakers, live.Summary);
        Assert.Single(live.Groups);
        Assert.Equal(["Office", "Garage", "Kitchen"], live.Groups[0].Rooms);
        Assert.True(LiveGrouping.MatchesDaily(live, allSpeakers: true, dailyRooms: null));
    }

    [Fact]
    public void Map_EveryRoomAlone_DoesNotClaimAllSpeakers()
    {
        var live = LiveGrouping.Map(
        [
            Member("Workshop", "w", coordinator: true),
            Member("Kitchen", "k", coordinator: true),
            Member("Office", "o", coordinator: true),
        ]);

        Assert.Equal("3 rooms ungrouped", live.Summary);
        Assert.Equal(["Kitchen", "Office", "Workshop"], live.Groups.Select(g => g.CoordinatorRoom));
        Assert.False(LiveGrouping.MatchesDaily(live, allSpeakers: true, dailyRooms: []));
    }

    [Fact]
    public void Map_TwoGroupsAndOneSolo_ListsGroupsThenTheCount()
    {
        var live = LiveGrouping.Map(
        [
            Member("Office", "o", coordinator: true),
            Member("Master Bath", "o", coordinator: false),
            Member("Kitchen", "k", coordinator: true),
            Member("Workshop", "k", coordinator: false),
            Member("Garage", "g", coordinator: true),
        ]);

        Assert.Equal("Kitchen + Workshop; Office + Master Bath; 1 room ungrouped", live.Summary);
    }

    [Fact]
    public void Map_EmptyOrBlankRooms_IsNoSpeakersAndNotDrift()
    {
        var live = LiveGrouping.Map([new SpeakerMembership("  ", "x", true), new SpeakerMembership(null!, "", false)]);

        Assert.Equal(LiveGrouping.NoSpeakers, live.Summary);
        Assert.Empty(live.Groups);
        Assert.True(LiveGrouping.MatchesDaily(live, allSpeakers: true, dailyRooms: null));
    }

    [Fact]
    public void Map_SingleRoom_UsesTheRoomName()
    {
        var live = LiveGrouping.Map([Member("Office", "o", coordinator: true)]);

        Assert.Equal("Office", live.Summary);
        Assert.True(LiveGrouping.MatchesDaily(live, allSpeakers: true, dailyRooms: null));
    }

    [Fact]
    public void MatchesDaily_SubsetSplitsOrGoesMissing_IsDrift()
    {
        var live = LiveGrouping.Map(
        [
            Member("Office", "o", coordinator: true),
            Member("Kitchen", "k", coordinator: true),
        ]);

        Assert.False(LiveGrouping.MatchesDaily(live, allSpeakers: false, dailyRooms: ["office", "Kitchen"]));
        Assert.False(LiveGrouping.MatchesDaily(live, allSpeakers: false, dailyRooms: ["Office", "Den"]));
        Assert.True(LiveGrouping.MatchesDaily(live, allSpeakers: false, dailyRooms: ["Kitchen"]));
    }

    [Fact]
    public void MatchesDaily_ExtraRoomsOutsideThePreset_DoNotCountAsDrift()
    {
        var live = LiveGrouping.Map(
        [
            Member("Office", "o", coordinator: true),
            Member("Master Bath", "o", coordinator: false),
            Member("Garage", "g", coordinator: true),
        ]);

        Assert.True(LiveGrouping.MatchesDaily(live, allSpeakers: false, dailyRooms: ["Office", "Master Bath"]));
        Assert.Equal(LiveGrouping.DriftMessage, "Not grouped as Daily group, tap Regroup");
    }

    [Fact]
    public void Map_BlankGroupId_DoesNotGlueUnrelatedRoomsTogether()
    {
        var live = LiveGrouping.Map(
        [
            Member("Office", "", coordinator: true),
            Member("Kitchen", " ", coordinator: true),
        ]);

        Assert.Equal("2 rooms ungrouped", live.Summary);
    }

    static SpeakerMembership Member(string room, string group, bool coordinator) =>
        new(room, group, coordinator);
}
