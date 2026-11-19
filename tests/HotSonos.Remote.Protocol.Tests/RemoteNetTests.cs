namespace HotSonos.Remote.Protocol.Tests;

public class RemoteNetTests
{
    [Fact]
    public void ChooseLanAddress_PicksTheSubnetThatHasASpeaker()
    {
        var ip = RemoteNet.ChooseLanAddress(
            ["172.22.0.1", "192.168.1.10"],
            ["192.168.1.202", "192.168.1.114"]);

        Assert.Equal("192.168.1.10", ip);
    }

    [Fact]
    public void ChooseLanAddress_ReturnsNullWhenNoSpeakerSharesASubnet()
    {
        var ip = RemoteNet.ChooseLanAddress(
            ["10.0.0.5"],
            ["192.168.1.202"]);

        Assert.Null(ip);
    }

    [Fact]
    public void ChooseLanAddress_ReturnsNullWithoutSpeakers()
    {
        Assert.Null(RemoteNet.ChooseLanAddress(["192.168.1.10"], []));
        Assert.Null(RemoteNet.ChooseLanAddress(["192.168.1.10"], null));
    }

    [Fact]
    public void IsAllowedArtUri_AcceptsSpeakerPort1400Only()
    {
        var speakers = new[] { "192.168.1.202" };
        Assert.True(RemoteNet.IsAllowedArtUri("http://192.168.1.202:1400/getaa?u=abc", speakers));
        Assert.False(RemoteNet.IsAllowedArtUri("http://192.168.1.50:1400/getaa", speakers));
        Assert.False(RemoteNet.IsAllowedArtUri("https://192.168.1.202:1400/getaa", speakers));
        Assert.False(RemoteNet.IsAllowedArtUri("http://192.168.1.202/getaa", speakers));
        Assert.False(RemoteNet.IsAllowedArtUri("http://192.168.1.202:1400/getaa", []));
        Assert.False(RemoteNet.IsAllowedArtUri("/getaa?u=abc", speakers));
        Assert.False(RemoteNet.IsAllowedArtUri("http://user:pass@192.168.1.202:1400/getaa", speakers));
    }

    [Fact]
    public void ArtRevision_IsStableAndEmptyWithoutArt()
    {
        Assert.Equal("", RemoteNet.ArtRevision(null));
        Assert.Equal("", RemoteNet.ArtRevision("  "));
        var a = RemoteNet.ArtRevision("http://192.168.1.202:1400/getaa?u=1");
        var b = RemoteNet.ArtRevision("http://192.168.1.202:1400/getaa?u=1");
        var c = RemoteNet.ArtRevision("http://192.168.1.202:1400/getaa?u=2");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(12, a.Length);
    }

    [Fact]
    public void PinMatches_RequiresTheExactPin()
    {
        Assert.True(RemoteNet.PinMatches("042815", "042815"));
        Assert.False(RemoteNet.PinMatches("042816", "042815"));
        Assert.False(RemoteNet.PinMatches("42815", "042815"));
        Assert.False(RemoteNet.PinMatches(null, "042815"));
        Assert.False(RemoteNet.PinMatches("042815", null));
        Assert.False(RemoteNet.PinMatches("", ""));
    }

    [Fact]
    public void TryParseAddress_AcceptsHostPortAndPastedTrayUrl()
    {
        Assert.True(RemoteNet.TryParseAddress("192.168.1.20", "42342", out var host, out var port, out var error));
        Assert.Equal("192.168.1.20", host);
        Assert.Equal(42342, port);
        Assert.Null(error);

        Assert.True(RemoteNet.TryParseAddress("http://192.168.1.20:42342", "80", out host, out port, out error));
        Assert.Equal("192.168.1.20", host);
        Assert.Equal(42342, port);

        Assert.False(RemoteNet.TryParseAddress("https://192.168.1.20:42342", "42342", out _, out _, out error));
        Assert.Equal("Use the http address from the tray.", error);
        Assert.False(RemoteNet.TryParseAddress("", "42342", out _, out _, out error));
        Assert.True(RemoteNet.TryParseAddress("living-room", "", out host, out port, out error));
        Assert.Equal("living-room", host);
        Assert.Equal(42342, port);
        Assert.True(RemoteNet.TryParseAddress("living-room", null, out host, out port, out error));
        Assert.Equal(42342, port);

        Assert.False(RemoteNet.TryParseAddress("192.168.1.20", "80", out _, out _, out error));
        Assert.False(RemoteNet.TryParseAddress("192.168.1.20/status", "42342", out _, out _, out _));
    }

    [Fact]
    public void IsSixDigitPin_KeepsLeadingZeros()
    {
        Assert.True(RemoteNet.IsSixDigitPin("042815"));
        Assert.False(RemoteNet.IsSixDigitPin("42815"));
        Assert.False(RemoteNet.IsSixDigitPin("04281A"));
        Assert.False(RemoteNet.IsSixDigitPin(null));
    }
}
