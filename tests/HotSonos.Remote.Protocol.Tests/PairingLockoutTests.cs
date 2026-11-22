namespace HotSonos.Remote.Protocol.Tests;

public class PairingLockoutTests
{
    [Fact]
    public void EightFailuresInsideTenMinutes_LocksForFifteenMinutes()
    {
        var time = new ManualTime();
        var lockout = new PairingLockout(time);

        for (var i = 0; i < 7; i++)
            Assert.False(lockout.RegisterFailure());
        Assert.False(lockout.IsLocked(out _));

        Assert.True(lockout.RegisterFailure());
        Assert.True(lockout.IsLocked(out var remaining));
        Assert.Equal(TimeSpan.FromMinutes(15), remaining);
        Assert.Contains("15 minutes", PairingLockout.LockedMessage(remaining), StringComparison.Ordinal);

        time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.True(lockout.IsLocked(out _));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(lockout.IsLocked(out _));
    }

    [Fact]
    public void AFailureAtNineMinutes_StillCountsInsideTheWindow()
    {
        var time = new ManualTime();
        var lockout = new PairingLockout(time);
        var start = time.Now;

        for (var i = 0; i < 7; i++)
            Assert.False(lockout.RegisterFailure());

        time.Now = start + TimeSpan.FromMinutes(9);
        Assert.True(lockout.RegisterFailure());
        Assert.True(lockout.IsLocked(out _));
    }

    [Fact]
    public void AFailureExactlyTenMinutesLater_StartsANewWindow()
    {
        var time = new ManualTime();
        var lockout = new PairingLockout(time);
        var start = time.Now;

        for (var i = 0; i < 7; i++)
            lockout.RegisterFailure();

        time.Now = start + PairingLockout.FailureWindow;
        Assert.False(lockout.RegisterFailure());
        Assert.False(lockout.IsLocked(out _));

        for (var i = 0; i < 6; i++)
            Assert.False(lockout.RegisterFailure());
        Assert.True(lockout.RegisterFailure());
        Assert.True(lockout.IsLocked(out _));
    }

    [Fact]
    public void Success_ClearsTheFailureWindow()
    {
        var lockout = new PairingLockout(new ManualTime());
        for (var i = 0; i < 7; i++)
            lockout.RegisterFailure();
        lockout.RegisterSuccess();
        for (var i = 0; i < 7; i++)
            Assert.False(lockout.RegisterFailure());
        Assert.False(lockout.IsLocked(out _));
    }

    [Fact]
    public void AttemptsDuringTheLock_DoNotExtendIt()
    {
        var time = new ManualTime();
        var lockout = new PairingLockout(time);
        var start = time.Now;
        for (var i = 0; i < 8; i++)
            lockout.RegisterFailure();

        time.Now = start + TimeSpan.FromMinutes(1);
        Assert.True(lockout.RegisterFailure());

        time.Now = start + PairingLockout.LockoutDuration;
        Assert.False(lockout.IsLocked(out _));
        Assert.False(lockout.RegisterFailure());
    }
}

sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
