using HotSonos.Core.Models;

namespace HotSonos.Core.Tests;

public class TopologyLayoutTests
{
    [Fact]
    public void Map_IssueSnapshot_FoldsTheSubAndListsEachSoloRoom()
    {
        var cards = TopologyLayout.Map(
        [
            Member("Office", "g-office", coordinator: true),
            Member("Master Bath", "g-office", coordinator: false),
            Hidden("Sub", "g-office", role: "SW", product: "Sub"),
            Member("Kitchen", "g-kitchen", coordinator: true),
            Member("Workshop", "g-workshop", coordinator: true),
            Member("Living Room", "g-living", coordinator: true),
            Member("Sun Room", "g-sun", coordinator: true),
            Member("Garage", "g-garage", coordinator: true),
            Member("Hobby", "g-hobby", coordinator: true),
            Member("Theater", "g-theater", coordinator: true),
            Member("Game Room", "g-game", coordinator: true),
        ]);

        Assert.Equal(9, cards.Count);
        var office = Assert.Single(cards, c => !c.Ungrouped);
        Assert.Equal("Office", office.CoordinatorRoom);
        Assert.Equal(["Office", "Master Bath"], office.Rooms.Select(r => r.Room));
        Assert.Equal(["Sub"], office.Rooms[0].Folded);
        Assert.Empty(office.Rooms[1].Folded);

        var solos = cards.Where(c => c.Ungrouped).Select(c => c.CoordinatorRoom).ToList();
        Assert.Equal(
            ["Game Room", "Garage", "Hobby", "Kitchen", "Living Room", "Sun Room", "Theater", "Workshop"],
            solos);
        Assert.All(cards.Where(c => c.Ungrouped), c => Assert.Single(c.Rooms));
    }

    [Fact]
    public void Map_NamedSub_FoldsUnderThatRoomNotTheCoordinator()
    {
        var cards = TopologyLayout.Map(
        [
            Member("Office", "g", coordinator: true),
            Member("Master Bath", "g", coordinator: false),
            Hidden("Master Bath Sub", "g", role: "SW", product: "Sub"),
        ]);

        var card = Assert.Single(cards);
        Assert.False(card.Ungrouped);
        Assert.Empty(card.Rooms.Single(r => r.Room == "Office").Folded);
        Assert.Equal(["Sub"], card.Rooms.Single(r => r.Room == "Master Bath").Folded);
    }

    [Fact]
    public void Map_InvisibleMateWithTheSameRoomName_StaysUnderThatRoom()
    {
        var cards = TopologyLayout.Map(
        [
            Member("Living Room", "g", coordinator: true),
            Hidden("Living Room", "g", role: "RF", product: "Era 100"),
        ]);

        var card = Assert.Single(cards);
        Assert.True(card.Ungrouped);
        Assert.Equal(["RF"], Assert.Single(card.Rooms).Folded);
    }

    [Fact]
    public void Map_BlankGroupIds_DoNotGlueUnrelatedRooms()
    {
        var cards = TopologyLayout.Map(
        [
            Member("Office", "", coordinator: true),
            Member("Kitchen", "  ", coordinator: true),
        ]);

        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.True(c.Ungrouped));
    }

    [Fact]
    public void PlayLabel_UsesTheWordsOnTheCard()
    {
        Assert.Equal("Playing", TopologyLayout.PlayLabel(SonosTransportState.Playing));
        Assert.Equal("Playing", TopologyLayout.PlayLabel(SonosTransportState.Transitioning));
        Assert.Equal("Paused", TopologyLayout.PlayLabel(SonosTransportState.PausedPlayback));
        Assert.Equal("Stopped", TopologyLayout.PlayLabel(SonosTransportState.Stopped));
        Assert.Equal("Unknown", TopologyLayout.PlayLabel(SonosTransportState.Unknown));
    }

    static TopologyMemberInput Member(string room, string group, bool coordinator) =>
        new(room, group, coordinator, false, null, null);

    static TopologyMemberInput Hidden(string room, string group, string role, string product) =>
        new(room, group, false, true, role, product);
}
