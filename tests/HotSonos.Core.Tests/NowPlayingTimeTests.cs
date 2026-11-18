using HotSonos.Core.Models;

namespace HotSonos.Core.Tests;

public class NowPlayingTimeTests
{
    [Fact]
    public void ParseSonosTime_reads_clock_and_rejects_placeholders()
    {
        Assert.Equal(TimeSpan.FromSeconds(225), NowPlaying.ParseSonosTime("0:03:45"));
        Assert.Equal(TimeSpan.FromMinutes(61) + TimeSpan.FromSeconds(2), NowPlaying.ParseSonosTime("1:01:02"));
        Assert.Null(NowPlaying.ParseSonosTime("NOT_IMPLEMENTED"));
        Assert.Null(NowPlaying.ParseSonosTime(null));
        Assert.Null(NowPlaying.ParseSonosTime("nope"));
    }
}
