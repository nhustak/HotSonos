namespace HotSonos.Remote.Protocol.Tests;

public class PhonePairLinkTests
{
    [Fact]
    public void TryFormat_WritesThePairingUri()
    {
        Assert.True(PhonePairLink.TryFormat("192.168.1.10", 42342, "042815", out var link, out var error));
        Assert.Null(error);
        Assert.Equal("hotsonos://pair?host=192.168.1.10&port=42342&pin=042815", link);
    }

    [Fact]
    public void TryParse_RoundTripsHostPortAndPin()
    {
        Assert.True(PhonePairLink.TryFormat("192.168.1.20", 42400, "000123", out var link, out _));
        Assert.True(PhonePairLink.TryParse(link, out var host, out var port, out var pin, out var error));
        Assert.Null(error);
        Assert.Equal("192.168.1.20", host);
        Assert.Equal(42400, port);
        Assert.Equal("000123", pin);
    }

    [Fact]
    public void TryParse_AcceptsCaseWhitespaceAndAMissingPort()
    {
        const string text = "  \"HOTSONOS://Pair?PIN=042815&HOST=192.168.1.10\"  ";
        Assert.True(PhonePairLink.TryParse(text, out var host, out var port, out var pin, out var error));
        Assert.Null(error);
        Assert.Equal("192.168.1.10", host);
        Assert.Equal(42342, port);
        Assert.Equal("042815", pin);
    }

    [Fact]
    public void TryParse_AcceptsATrailingSlash()
    {
        Assert.True(PhonePairLink.TryParse(
            "hotsonos://pair/?host=10.0.0.8&port=42342&pin=123456",
            out var host, out var port, out var pin, out var error));
        Assert.Null(error);
        Assert.Equal("10.0.0.8", host);
        Assert.Equal(42342, port);
        Assert.Equal("123456", pin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://192.168.1.10:42342")]
    [InlineData("hotsonos://other?host=192.168.1.10&port=42342&pin=123456")]
    [InlineData("hotsonos://pair?port=42342&pin=123456")]
    [InlineData("hotsonos://pair?host=192.168.1.10&port=42342&pin=12345")]
    [InlineData("hotsonos://pair?host=192.168.1.10&port=80&pin=123456")]
    [InlineData("hotsonos://pair?host=https://192.168.1.10&pin=123456")]
    public void TryParse_RejectsALinkThatIsNotAPairing(string text)
    {
        Assert.False(PhonePairLink.TryParse(text, out _, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void PreferenceKeys_StayStableAcrossUpdates()
    {
        Assert.Equal("pc.host", PhonePairLink.HostPreferenceKey);
        Assert.Equal("pc.port", PhonePairLink.PortPreferenceKey);
        Assert.Equal("pc.pin", PhonePairLink.PinPreferenceKey);
    }
}
