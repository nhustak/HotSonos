using System.Net;

namespace HotSonos.Remote.Protocol.Tests;

public class PairingAttemptTests
{
    const string Pin = "042815";

    [Fact]
    public void PublicAddress_IsForbiddenAndDoesNotCount()
    {
        var time = new ManualTime();
        var lockout = new PairingLockout(time);
        var stored = PairingPassword.Hash("house-password");

        var denied = PairingAttempt.Evaluate(IPAddress.Parse("8.8.8.8"), "house-password", stored, Pin, lockout);
        Assert.Equal(PairingAttemptKind.Forbidden, denied.Kind);
        Assert.Equal(PairingAttempt.ForbiddenMessage, denied.Error);
        Assert.Null(denied.Pin);

        var firstMiss = PairingAttempt.Evaluate(IPAddress.Parse("192.168.1.20"), "nope", stored, Pin, lockout);
        Assert.Equal(PairingAttemptKind.Unauthorized, firstMiss.Kind);
        Assert.Equal(PairingAttempt.WrongPasswordMessage, firstMiss.Error);
        Assert.False(lockout.IsLocked(out _));
    }

    [Fact]
    public void LanPassword_ReturnsThePinAndClearsFailures()
    {
        var lockout = new PairingLockout(new ManualTime());
        var stored = PairingPassword.Hash("house-password");
        for (var i = 0; i < 7; i++)
            lockout.RegisterFailure();

        var ok = PairingAttempt.Evaluate(IPAddress.Parse("10.1.2.3"), "house-password", stored, Pin, lockout);
        Assert.Equal(PairingAttemptKind.Ok, ok.Kind);
        Assert.Equal(Pin, ok.Pin);
        Assert.Null(ok.Error);

        for (var i = 0; i < 7; i++)
            Assert.False(lockout.RegisterFailure());
        Assert.False(lockout.IsLocked(out _));
    }

    [Fact]
    public void EighthLanFailure_LocksEvenACorrectPasswordUntilTheWindowEnds()
    {
        var time = new ManualTime();
        var lockout = new PairingLockout(time);
        var stored = PairingPassword.Hash("house-password");
        var lan = IPAddress.Parse("192.168.1.20");
        for (var i = 0; i < 7; i++)
            Assert.False(lockout.RegisterFailure());

        var eighth = PairingAttempt.Evaluate(lan, "nope", stored, Pin, lockout);
        Assert.Equal(PairingAttemptKind.Locked, eighth.Kind);
        Assert.Contains("15 minutes", eighth.Error, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(15), eighth.RetryAfter);

        var during = PairingAttempt.Evaluate(lan, "house-password", stored, Pin, lockout);
        Assert.Equal(PairingAttemptKind.Locked, during.Kind);
        Assert.Null(during.Pin);

        time.Advance(TimeSpan.FromMinutes(15));
        var after = PairingAttempt.Evaluate(lan, "house-password", stored, Pin, lockout);
        Assert.Equal(PairingAttemptKind.Ok, after.Kind);
        Assert.Equal(Pin, after.Pin);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.4.4")]
    [InlineData("172.16.5.5")]
    public void PrivateRanges_CanPair(string ip)
    {
        var stored = PairingPassword.Hash("house-password");
        var ok = PairingAttempt.Evaluate(IPAddress.Parse(ip), "house-password", stored, Pin, new PairingLockout());
        Assert.Equal(PairingAttemptKind.Ok, ok.Kind);
        Assert.Equal(Pin, ok.Pin);
    }
}
