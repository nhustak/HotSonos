using System.Net;

namespace HotSonos.Remote.Protocol.Tests;

public class RemoteDiscoveryTests
{
    [Fact]
    public void Reply_RoundTripsTheAddressAndPortAndNotAPin()
    {
        var text = RemoteDiscovery.BuildReply("192.168.1.20", 42342);
        Assert.Equal("HS1 192.168.1.20 42342", text);
        Assert.True(RemoteDiscovery.TryParseReply(text, out var host, out var port));
        Assert.Equal("192.168.1.20", host);
        Assert.Equal(42342, port);
        Assert.DoesNotContain("042815", text);
    }

    [Fact]
    public void Probe_IsNotAReply()
    {
        Assert.True(RemoteDiscovery.IsProbe("HS1?"));
        Assert.True(RemoteDiscovery.IsProbe("  HS1?\n"));
        Assert.False(RemoteDiscovery.IsProbe(RemoteDiscovery.BuildReply("192.168.1.20", 42342)));
        Assert.False(RemoteDiscovery.TryParseReply("HS1?", out _, out _));
        Assert.False(RemoteDiscovery.TryParseReply("HS1 192.168.1.20 42342 042815", out _, out _));
        Assert.False(RemoteDiscovery.TryParseReply("HS1 192.168.1.20 80", out _, out _));
    }

    [Fact]
    public void ProbeTargets_UseTheLocalSubnetBroadcast()
    {
        var directed = RemoteDiscovery.DirectedBroadcast("192.168.1.20");
        Assert.Equal(IPAddress.Parse("192.168.1.255"), directed);
        Assert.Null(RemoteDiscovery.DirectedBroadcast("127.0.0.1"));
        Assert.Null(RemoteDiscovery.DirectedBroadcast("169.254.1.1"));

        var targets = RemoteDiscovery.ProbeTargets(["192.168.1.20", "10.0.2.15", "not-an-ip"]);
        Assert.Contains(IPAddress.Broadcast, targets);
        Assert.Contains(IPAddress.Parse("192.168.1.255"), targets);
        Assert.Contains(IPAddress.Parse("10.0.2.255"), targets);
    }
}

public class RemoteFailureTextTests
{
    [Fact]
    public void Classify_SeparatesTimeoutFromUnreachableAndAWrongPinSentence()
    {
        Assert.Equal(RemoteFailureKind.Timeout, RemoteFailureText.Classify(callerCancelled: false, TransportFault.Canceled));
        Assert.Equal(RemoteFailureKind.Cancelled, RemoteFailureText.Classify(callerCancelled: true, TransportFault.Canceled));
        Assert.Equal(RemoteFailureKind.Unreachable, RemoteFailureText.Classify(false, TransportFault.Socket));

        Assert.StartsWith("Wrong PIN", RemoteFailureText.Describe(RemoteFailureKind.WrongPin, "ignored"));
        Assert.Contains("Timed out", RemoteFailureText.Describe(RemoteFailureKind.Timeout, null));
        var unreachable = RemoteFailureText.Describe(RemoteFailureKind.Unreachable, "No route to host");
        Assert.Contains("Unreachable", unreachable);
        Assert.Contains("No route to host", unreachable);
    }
}

public class LibraryPathRulesTests
{
    [Fact]
    public void TryNormalize_KeepsUncRootsAndDropsAWebAddress()
    {
        var ok = LibraryPathRules.TryNormalize(
            [new LibraryRootEdit(@"\\nas\music\Sonos\", true), new LibraryRootEdit(@"\\nas\music\Sonos", false), new LibraryRootEdit("  ", false)],
            @"\\nas\masters\",
            out var roots,
            out var master,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(@"\\nas\music\Sonos", Assert.Single(roots).Path);
        Assert.True(roots[0].InDaily);
        Assert.Equal(@"\\nas\masters", master);

        Assert.False(LibraryPathRules.TryNormalize(
            [new LibraryRootEdit("https://example/music", true)],
            null,
            out _,
            out _,
            out error));
        Assert.Contains("web address", error);
    }

    [Fact]
    public void MasterEdit_AllowsOneSharedMasterAndRejectsASplitMap()
    {
        Assert.True(LibraryPathRules.MasterEditAllowed(1, @"\\nas\a", @"\\nas\b"));
        Assert.True(LibraryPathRules.MasterEditAllowed(2, @"\\nas\a\", @"\\nas\a"));
        Assert.False(LibraryPathRules.MasterEditAllowed(2, @"\\nas\a", @"\\nas\b"));
    }
}
