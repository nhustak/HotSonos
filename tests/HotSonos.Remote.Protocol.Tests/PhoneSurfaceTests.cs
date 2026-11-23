namespace HotSonos.Remote.Protocol.Tests;

public class RoomNowPlayingTests
{
    static readonly IReadOnlyList<RemoteTopologyGroupDto> House =
    [
        Group("g-office", "Office", "Playing", "Office", "Master Bath"),
        Group("g-kitchen", "Kitchen", "Paused", "Kitchen"),
        Group("g-garage", "Garage", "Stopped", "Garage"),
    ];

    [Fact]
    public void ActiveGroup_ShowsTheTrackOnEveryRoomInIt()
    {
        Assert.Equal(
            "Man Size Love — The Pixies",
            RoomNowPlaying.Line("Office", House, "Office", "Man Size Love", "The Pixies"));
        Assert.Equal(
            "Man Size Love — The Pixies",
            RoomNowPlaying.Line("Master Bath", House, "Office", "Man Size Love", "The Pixies"));
    }

    [Fact]
    public void OtherGroup_ShowsPlayingOrPausedAndHidesSilence()
    {
        Assert.Equal("Paused", RoomNowPlaying.Line("Kitchen", House, "Office", "Man Size Love", "The Pixies"));
        Assert.Null(RoomNowPlaying.Line("Garage", House, "Office", "Man Size Love", "The Pixies"));
        Assert.Equal("Playing", RoomNowPlaying.Line("Office", House, "Kitchen", null, " "));
    }

    [Fact]
    public void NoLayout_ShowsTheTrackOnlyOnTheActiveRoom()
    {
        Assert.Equal("Man Size Love", RoomNowPlaying.Line("Office", null, "Office", "Man Size Love", null));
        Assert.Null(RoomNowPlaying.Line("Kitchen", null, "Office", "Man Size Love", "The Pixies"));
        Assert.Null(RoomNowPlaying.Line("  ", House, "Office", "Man Size Love", "The Pixies"));
    }

    [Fact]
    public void Track_SkipsBlankSides()
    {
        Assert.Null(RoomNowPlaying.Track(null, "  "));
        Assert.Equal("Only", RoomNowPlaying.Track(" Only ", null));
        Assert.Equal("Song — Band", RoomNowPlaying.Track("Song", "Band"));
    }

    static RemoteTopologyGroupDto Group(string id, string coordinator, string state, params string[] rooms) =>
        new(
            id,
            coordinator,
            rooms.Length < 2,
            state,
            rooms.Select(room => new RemoteTopologyRoomDto(room, room == coordinator, [])).ToList());
}

public class AboutCopyTests
{
    [Fact]
    public void Identity_IsTheShippedApp()
    {
        Assert.Equal("HotSonos", AboutCopy.Name);
        Assert.Equal("com.tangenttech.hotsonos", AboutCopy.AppId);
        Assert.Equal("https://hotsonos.com", AboutCopy.SiteUrl);
        Assert.Equal("https://github.com/nhustak/HotSonos", AboutCopy.GitHubUrl);
        Assert.Contains("remote", AboutCopy.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("standalone", AboutCopy.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sonos is a trademark of Sonos, Inc.", AboutCopy.Trademark);
    }

    [Fact]
    public void VersionLine_UsesAppInfoTextAndSaysWhenItIsMissing()
    {
        Assert.Equal("Version 1.0.0.76", AboutCopy.VersionLine(" 1.0.0.76 "));
        Assert.Equal("Version unavailable", AboutCopy.VersionLine("  "));
        Assert.Equal("Version unavailable", AboutCopy.VersionLine(null));
    }
}

public class PhoneRowActionTests
{
    [Fact]
    public void LibraryRows_DoNotIncludeAQueueButton()
    {
        Assert.Empty(LibraryRowActions.Buttons);
        Assert.Equal(["Wipe queue"], LibraryRowActions.Sheet(tags: false));
        Assert.Equal(["Wipe queue", "Toggle a tag…"], LibraryRowActions.Sheet(tags: true));
        Assert.DoesNotContain("Queue", LibraryRowActions.Sheet(tags: true));
        Assert.DoesNotContain("Play", LibraryRowActions.Sheet(tags: true));
    }

    [Fact]
    public void LibrarySongRows_AreCompactWithNoOnRowButtons()
    {
        Assert.Equal(48, LibraryRowActions.RowHeight);
        var source = Source("src", "HotSonos.Remote", "Views", "LibraryPage.cs");
        Assert.Contains("LibraryRowActions.RowHeight", source, StringComparison.Ordinal);
        Assert.Contains("MakeActivable", source, StringComparison.Ordinal);
        Assert.Contains("···", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteUi.Button(LibraryRowActions.Play", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteUi.Button(LibraryRowActions.Wipe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("""RemoteUi.Button("Toggle a tag""", source, StringComparison.Ordinal);
        Assert.DoesNotContain("static Grid ActionRow", source, StringComparison.Ordinal);
    }

    static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new InvalidOperationException("Could not find " + Path.Combine(parts));
    }

    [Fact]
    public void RoomSheet_MovesCoordinatorAndRebootOffTheRow()
    {
        Assert.Equal(["Make coordinator", "Reboot"], RoomRowActions.For(drivePc: true));
        Assert.Equal(["Make coordinator", "Leave group"], RoomRowActions.For(drivePc: false));
        Assert.DoesNotContain("Reboot", RoomRowActions.For(drivePc: false));
    }
}
