using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class WearWireTests
{
    [Theory]
    [InlineData("play", "play")]
    [InlineData("  Pause ", "pause")]
    [InlineData("NEXT", "next")]
    [InlineData("previous", "previous")]
    [InlineData("volume-up", "volume-up")]
    [InlineData("VOLUME-DOWN", "volume-down")]
    [InlineData("refresh", "refresh")]
    [InlineData("stop", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Commands_are_the_watch_verbs(string? payload, string? expected) =>
        Assert.Equal(expected, WearWire.ParseCommand(payload));

    [Fact]
    public void Volume_steps_by_the_phone_step_and_clamps()
    {
        Assert.Equal(42, WearWire.StepVolume(40, 1, PhoneVolume.SpeakerStep));
        Assert.Equal(38, WearWire.StepVolume(40, -1, PhoneVolume.SpeakerStep));
        Assert.Equal(100, WearWire.StepVolume(99, 1, PhoneVolume.SpeakerStep));
        Assert.Equal(0, WearWire.StepVolume(1, -1, PhoneVolume.SpeakerStep));
        Assert.Equal(40, WearWire.StepVolume(40, 0, PhoneVolume.SpeakerStep));
        Assert.Equal(41, WearWire.StepVolume(40, 1, 0));
    }

    [Fact]
    public void Volume_target_waits_for_the_poll_then_lets_go()
    {
        Assert.Null(WearWire.ReconcileTarget(null, 40, 0));
        Assert.Null(WearWire.ReconcileTarget(40, 40, 0));
        Assert.Equal(42, WearWire.ReconcileTarget(42, 40, 0));
        Assert.Equal(42, WearWire.ReconcileTarget(42, 40, WearWire.VolumeHoldMs - 1));
        Assert.Null(WearWire.ReconcileTarget(42, 40, WearWire.VolumeHoldMs));
        Assert.Equal(42, WearWire.ReconcileTarget(42, null, 0));
        Assert.Null(WearWire.ReconcileTarget(42, null, WearWire.VolumeHoldMs));
    }

    [Fact]
    public void State_json_uses_the_watch_field_names()
    {
        var playing = MediaControlMapping.FromStatus(Sample("Afternoon", "Ada", "Playing", 40));
        Assert.Equal(
            """{"title":"Afternoon","artist":"Ada","playing":true,"volume":40}""",
            WearWire.EncodeState(playing));

        var paused = MediaControlMapping.FromStatus(Sample("Afternoon", "Ada", "PausedPlayback", null));
        Assert.Equal(
            """{"title":"Afternoon","artist":"Ada","playing":false,"volume":null}""",
            WearWire.EncodeState(paused));

        var overridden = WearWire.EncodeState(playing, 55);
        Assert.Contains("\"volume\":55", overridden, StringComparison.Ordinal);

        var quoted = MediaControlMapping.FromStatus(Sample("Say \"hi\"", "Ada", "Stopped", 10));
        var json = WearWire.EncodeState(quoted);
        var decoded = WearWire.DecodeState(json);
        Assert.NotNull(decoded);
        Assert.Equal("Say \"hi\"", decoded.Title);
        Assert.False(decoded.Playing);
        Assert.Equal(10, decoded.Volume);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    public void Bad_state_json_is_ignored(string? json) =>
        Assert.Null(WearWire.DecodeState(json));

    [Fact]
    public void Watch_source_uses_the_same_path_commands_and_step()
    {
        var source = WatchKotlin();
        Assert.Contains($"\"{WearWire.Path}\"", source, StringComparison.Ordinal);
        foreach (var command in new[]
        {
            WearWire.Play,
            WearWire.Pause,
            WearWire.Next,
            WearWire.Previous,
            WearWire.VolumeUp,
            WearWire.VolumeDown,
            WearWire.Refresh,
        })
            Assert.Contains($"\"{command}\"", source, StringComparison.Ordinal);

        Assert.Contains("\"title\"", source, StringComparison.Ordinal);
        Assert.Contains("\"artist\"", source, StringComparison.Ordinal);
        Assert.Contains("\"playing\"", source, StringComparison.Ordinal);
        Assert.Contains("\"volume\"", source, StringComparison.Ordinal);
        Assert.Contains($"const val UNKNOWN_VOLUME = {WearWire.UnknownVolume}", source, StringComparison.Ordinal);
        Assert.Contains($"const val VOLUME_STEP = {PhoneVolume.SpeakerStep}", source, StringComparison.Ordinal);
    }

    static string WatchKotlin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "HotSonos.Wear", "app", "src", "main", "kotlin");
            if (!Directory.Exists(candidate))
                continue;
            var files = Directory.GetFiles(candidate, "*.kt", SearchOption.AllDirectories);
            return string.Join('\n', files.Select(File.ReadAllText));
        }

        throw new InvalidOperationException("Wear Kotlin sources were not found above the test output.");
    }

    static RemoteStatusResponse Sample(string title, string artist, string state, int? volume)
    {
        var now = new RemoteNowPlayingDto(title, artist, null, state, "Library", 1, 2, false, "");
        return new RemoteStatusResponse(true, "test", "Office", "Office", [], [], now, volume, false, false, false);
    }
}
