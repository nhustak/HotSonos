using HotSonos.Core;

namespace HotSonos.Core.Tests;

public class NormalizeTrackKeyTests
{
    [Theory]
    [InlineData(
        "x-file-cifs://192.168.1.111/Music/Sonos/John%20Williams/John%20Williams%20-%20Tears%20in%20Rain%20(Dialogue%20%26%20Early%20Temp%20Tracked%20Music).flac",
        "192.168.1.111/music/sonos/john williams/john williams - tears in rain (dialogue & early temp tracked music).flac")]
    [InlineData(
        "x-file-cifs://192.168.1.111/Music/Sonos/Phil Collins/Phil Collins - Sussudio.flac",
        "192.168.1.111/music/sonos/phil collins/phil collins - sussudio.flac")]
    public void NormalizeTrackKey_strips_scheme_decodes_and_lowercases(string uri, string expected)
    {
        Assert.Equal(expected, SonosController.NormalizeTrackKey(uri));
    }

    [Fact]
    public void NormalizeTrackKey_is_idempotent()
    {
        const string uri =
            "x-file-cifs://192.168.1.111/Music/Sonos/John%20Williams/John%20Williams%20-%20Tears%20in%20Rain.flac";
        var once = SonosController.NormalizeTrackKey(uri);
        var twice = SonosController.NormalizeTrackKey(once);
        Assert.Equal(once, twice);
    }
}
