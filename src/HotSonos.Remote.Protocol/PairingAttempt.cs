using System.Net;

namespace HotSonos.Remote.Protocol;

public enum PairingAttemptKind
{
    Ok,
    Unauthorized,
    Locked,
    Forbidden,
    Unavailable,
}

/// <summary>One decision for <c>POST /api/pair</c>. The password is not copied into the result.</summary>
public readonly record struct PairingAttempt(
    PairingAttemptKind Kind,
    string? Error,
    string? Pin,
    TimeSpan RetryAfter)
{
    public const string ForbiddenMessage = "Pairing is only available on the local network.";
    public const string WrongPasswordMessage = "Wrong password.";

    public static PairingAttempt Evaluate(
        IPAddress? remote,
        string? password,
        PairingPasswordRecord stored,
        string? pin,
        PairingLockout lockout)
    {
        if (!RemoteNet.IsPrivateLan(remote))
            return new PairingAttempt(PairingAttemptKind.Forbidden, ForbiddenMessage, null, TimeSpan.Zero);

        if (lockout.IsLocked(out var remaining))
            return Locked(remaining);

        var candidate = password is { Length: > 0 and <= PairingPassword.MaxLength } ? password : "";
        if (!PairingPassword.Verify(candidate, stored))
        {
            if (lockout.RegisterFailure() && lockout.IsLocked(out remaining))
                return Locked(remaining);
            return new PairingAttempt(PairingAttemptKind.Unauthorized, WrongPasswordMessage, null, TimeSpan.Zero);
        }

        lockout.RegisterSuccess();
        if (!RemoteNet.IsSixDigitPin(pin))
            return new PairingAttempt(PairingAttemptKind.Unavailable, "Pairing is not ready on this PC.", null, TimeSpan.Zero);

        return new PairingAttempt(PairingAttemptKind.Ok, null, pin, TimeSpan.Zero);
    }

    static PairingAttempt Locked(TimeSpan remaining) =>
        new(PairingAttemptKind.Locked, PairingLockout.LockedMessage(remaining), null, remaining);
}
