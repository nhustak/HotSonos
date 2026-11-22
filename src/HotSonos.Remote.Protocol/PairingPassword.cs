using System.Security.Cryptography;

namespace HotSonos.Remote.Protocol;

/// <summary>
/// Salted PBKDF2-SHA256 record for the phone pairing password.
/// The password itself is never stored.
/// </summary>
public sealed record PairingPasswordRecord(string Salt, int Iterations, string Hash);

/// <summary>
/// Hashes and checks the one pairing password. Comparison of the derived key is fixed-time.
/// </summary>
public static class PairingPassword
{
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const int Iterations = 100_000;
    public const int MaxIterations = 1_000_000;
    public const int MaxLength = 128;

    /// <summary>
    /// Used only when settings have no stored hash. Hashed on first run.
    /// Not written to settings, the log, or the phone.
    /// </summary>
    public const string DefaultPassword = "bagel";

    static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(SaltBytes);
    static readonly byte[] DummyHash = RandomNumberGenerator.GetBytes(HashBytes);

    public static bool TryValidate(string? password, out string? error)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Enter a password.";
            return false;
        }

        if (password.Length > MaxLength)
        {
            error = "Password must be 128 characters or fewer.";
            return false;
        }

        error = null;
        return true;
    }

    public static PairingPasswordRecord Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations);
        return new PairingPasswordRecord(Convert.ToBase64String(salt), Iterations, Convert.ToBase64String(hash));
    }

    public static PairingPasswordRecord SeedDefault() => Hash(DefaultPassword);

    /// <summary>Keeps a valid stored hash. Otherwise hashes the built-in default.</summary>
    public static PairingPasswordRecord Ensure(string? salt, int iterations, string? hash)
    {
        var stored = new PairingPasswordRecord(salt ?? "", iterations, hash ?? "");
        return IsStored(stored) ? stored : SeedDefault();
    }

    public static bool IsStored(string? salt, int iterations, string? hash) =>
        IsStored(new PairingPasswordRecord(salt ?? "", iterations, hash ?? ""));

    public static bool IsStored(PairingPasswordRecord stored) =>
        TryRead(stored, out _, out _, out _);

    public static bool Verify(string? password, PairingPasswordRecord stored)
    {
        var configured = TryRead(stored, out var salt, out var iterations, out var expected);
        if (!configured)
        {
            salt = DummySalt;
            iterations = Iterations;
            expected = DummyHash;
        }

        var actual = Derive(password ?? "", salt, iterations);
        var match = HashesMatch(actual, expected);
        return configured && match;
    }

    /// <summary>
    /// Fixed-time compare of two derived keys. A different length still runs a full compare and does not match.
    /// </summary>
    public static bool HashesMatch(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        if (actual.Length != HashBytes || expected.Length != HashBytes)
        {
            _ = CryptographicOperations.FixedTimeEquals(
                actual.Length == HashBytes ? actual : DummyHash,
                DummyHash);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    static bool TryRead(PairingPasswordRecord stored, out byte[] salt, out int iterations, out byte[] expected)
    {
        salt = [];
        iterations = 0;
        expected = [];
        if (stored.Iterations is < Iterations or > MaxIterations)
            return false;
        if (string.IsNullOrWhiteSpace(stored.Salt) || string.IsNullOrWhiteSpace(stored.Hash))
            return false;

        try
        {
            salt = Convert.FromBase64String(stored.Salt);
            expected = Convert.FromBase64String(stored.Hash);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltBytes || expected.Length != HashBytes)
            return false;

        iterations = stored.Iterations;
        return true;
    }

    static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
