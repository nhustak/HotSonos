using HotSonos.Core.Models;

namespace HotSonos.Core.Tests;

public class SonosTransportStateParserTests
{
    [Theory]
    [InlineData("STOPPED", SonosTransportState.Stopped)]
    [InlineData("PLAYING", SonosTransportState.Playing)]
    [InlineData("PAUSED_PLAYBACK", SonosTransportState.PausedPlayback)]
    [InlineData("TRANSITIONING", SonosTransportState.Transitioning)]
    [InlineData("NO_MEDIA_PRESENT", SonosTransportState.Unknown)]
    [InlineData(null, SonosTransportState.Unknown)]
    public void Parse_maps_known_and_unknown_values(string? raw, SonosTransportState expected)
    {
        Assert.Equal(expected, SonosTransportStateParser.Parse(raw));
    }
}
