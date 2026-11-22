namespace HotSonos.Remote.Protocol;

/// <summary>
/// Eight failed pairing attempts inside ten minutes locks pairing for fifteen minutes.
/// The clock is injectable so the window can be tested without waiting.
/// </summary>
public sealed class PairingLockout
{
    public const int FailureLimit = 8;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    readonly object _gate = new();
    readonly TimeProvider _time;
    readonly Queue<DateTimeOffset> _failures = new();
    DateTimeOffset? _lockedUntil;

    public PairingLockout(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool IsLocked(out TimeSpan remaining)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lockedUntil is { } until && now < until)
            {
                remaining = until - now;
                return true;
            }

            _lockedUntil = null;
            remaining = TimeSpan.Zero;
            return false;
        }
    }

    /// <summary>Records one failure. True when this failure starts a lock, or pairing is already locked.</summary>
    public bool RegisterFailure()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lockedUntil is { } until && now < until)
                return true;

            _lockedUntil = null;
            while (_failures.Count > 0 && now - _failures.Peek() >= FailureWindow)
                _failures.Dequeue();

            _failures.Enqueue(now);
            if (_failures.Count < FailureLimit)
                return false;

            _lockedUntil = now + LockoutDuration;
            _failures.Clear();
            return true;
        }
    }

    public void RegisterSuccess()
    {
        lock (_gate)
        {
            _failures.Clear();
            _lockedUntil = null;
        }
    }

    public static string LockedMessage(TimeSpan remaining)
    {
        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        if (minutes < 1)
            minutes = 1;
        return minutes == 1
            ? "Too many attempts. Pairing is locked. Try again in 1 minute."
            : $"Too many attempts. Pairing is locked. Try again in {minutes} minutes.";
    }
}
