namespace HotSonos.Remote.Protocol.Tests;

public class PhoneLinkPolicyTests
{
    [Fact]
    public void Auto_UsesThePcWhenItAnswersAndSpeakersWhenItDoesNot()
    {
        Assert.Equal(PhoneLinkOutcome.Pc, PhoneLinkPolicy.Choose(PhoneLinkPreference.Auto, paired: true, pcReachable: true));
        Assert.Equal(PhoneLinkOutcome.Speakers, PhoneLinkPolicy.Choose(PhoneLinkPreference.Auto, paired: true, pcReachable: false));
        Assert.Equal(PhoneLinkOutcome.Speakers, PhoneLinkPolicy.Choose(PhoneLinkPreference.Auto, paired: false, pcReachable: false));
    }

    [Fact]
    public void PcOnly_StaysOnThePcWhenItIsDown()
    {
        Assert.Equal(PhoneLinkOutcome.PcDown, PhoneLinkPolicy.Choose(PhoneLinkPreference.Pc, paired: true, pcReachable: false));
    }

    [Fact]
    public void Standalone_NeverAsksThePc()
    {
        Assert.Equal(PhoneLinkOutcome.Speakers, PhoneLinkPolicy.Choose(PhoneLinkPreference.Standalone, paired: true, pcReachable: true));
    }

    [Fact]
    public void Banner_NamesThePcOrTheRoom()
    {
        Assert.Equal("PC · 192.168.1.20", PhoneLinkPolicy.Banner(PhoneLinkOutcome.Pc, "192.168.1.20", "Kitchen"));
        Assert.Equal("Speakers · Kitchen", PhoneLinkPolicy.Banner(PhoneLinkOutcome.Speakers, "192.168.1.20", "Kitchen"));
        Assert.Equal("PC unreachable", PhoneLinkPolicy.Banner(PhoneLinkOutcome.PcDown, "192.168.1.20", "Kitchen"));
    }
}


public class DailyGroupLogicTests
{
    [Fact]
    public void SavePreset_ReplacesTheSameNameAndKeepsTheRoomList()
    {
        var model = new DailyGroupModel
        {
            AllSpeakers = true,
            Presets = [new DailyPresetModel { Name = "Downstairs", Rooms = ["Kitchen"] }],
        };

        var error = DailyGroupLogic.SavePreset(model, "downstairs", ["Office", "Kitchen", "Office"]);

        Assert.Null(error);
        Assert.Single(model.Presets);
        Assert.Equal("downstairs", model.Presets[0].Name);
        Assert.Equal(["Kitchen", "Office"], model.Presets[0].Rooms);
    }

    [Fact]
    public void SavePreset_RejectsAnEmptyNameOrNoRooms()
    {
        var model = new DailyGroupModel();
        Assert.Equal("Type a name for the speaker group.", DailyGroupLogic.SavePreset(model, "  ", ["Kitchen"]));
        Assert.Equal("Check at least one room before saving a group.", DailyGroupLogic.SavePreset(model, "Patio", []));
        Assert.Empty(model.Presets);
    }

    [Fact]
    public void SelectPreset_CopiesRoomsAndTurnsAllOff()
    {
        var model = new DailyGroupModel
        {
            AllSpeakers = true,
            Rooms = ["Kitchen"],
            Presets = [new DailyPresetModel { Name = "Office hours", Rooms = ["Office", "Den"] }],
        };

        Assert.Null(DailyGroupLogic.SelectPreset(model, "Office hours", on: true));
        Assert.False(model.AllSpeakers);
        Assert.Equal(["Den", "Office"], model.Rooms);
        Assert.Equal("Office hours", DailyGroupLogic.ActivePresetName(model));

        Assert.Null(DailyGroupLogic.SelectPreset(model, "Office hours", on: false));
        Assert.True(model.AllSpeakers);
        Assert.Null(DailyGroupLogic.ActivePresetName(model));
    }

    [Fact]
    public void SetAll_SeedsARoomOnlyWhenTheCustomListIsEmpty()
    {
        var model = new DailyGroupModel { AllSpeakers = true, Rooms = ["Kitchen"] };
        DailyGroupLogic.SetAll(model, all: false, seedRoom: "Office");
        Assert.Equal(["Kitchen"], model.Rooms);

        model.Rooms.Clear();
        DailyGroupLogic.SetAll(model, all: false, seedRoom: "Office");
        Assert.Equal(["Office"], model.Rooms);

        DailyGroupLogic.SetAll(model, all: true, seedRoom: null);
        Assert.True(model.AllSpeakers);
        Assert.Equal(["Office"], model.Rooms);
    }

    [Fact]
    public void RemovePreset_LeavesTheCurrentRoomSelection()
    {
        var model = new DailyGroupModel
        {
            AllSpeakers = false,
            Rooms = ["Office"],
            Presets = [new DailyPresetModel { Name = "Office", Rooms = ["Office"] }],
        };

        Assert.Null(DailyGroupLogic.RemovePreset(model, "office"));
        Assert.Empty(model.Presets);
        Assert.Equal(["Office"], model.Rooms);
        Assert.Equal("Group not found.", DailyGroupLogic.RemovePreset(model, "office"));
    }
}

public class RemoteSettingsRulesTests
{
    [Fact]
    public void Validate_AcceptsAPhoneSettingsPatch()
    {
        var error = RemoteSettingsRules.Validate(new RemoteSettingsBody
        {
            VolumeStep = 5,
            LevelVolumePercent = 20,
            WakeMinutes = 7 * 60,
            WakeSource = "favorite",
            ShuffleQueueTracks = 80,
            ControlShuffleSource = "tag:favs",
        });

        Assert.Null(error);
        Assert.Equal("Favorite", RemoteSettingsRules.CanonicalWakeSource("favorite"));
    }

    [Fact]
    public void Validate_RejectsOutOfRangeAndUnknownShuffleSources()
    {
        Assert.Equal("Volume step must be 1 to 50.", RemoteSettingsRules.Validate(new RemoteSettingsBody { VolumeStep = 0 }));
        Assert.Equal(
            "Shuffle source must be all, folder:path, tag:key, or genre:name.",
            RemoteSettingsRules.Validate(new RemoteSettingsBody { ControlShuffleSource = "file:c:\\music" }));
        Assert.False(RemoteSettingsRules.IsShuffleSource("folder:"));
        Assert.True(RemoteSettingsRules.IsShuffleSource("all"));
    }

    [Fact]
    public void SettingsBody_SerializesCamelCase()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new RemoteSettingsBody { VolumeStep = 5, ShowGenres = false }, RemoteApi.Json);
        Assert.Contains("\"volumeStep\":5", json);
        Assert.Contains("\"showGenres\":false", json);
        Assert.DoesNotContain("remotePin", json, StringComparison.OrdinalIgnoreCase);
    }
}
