namespace HotSonos.Remote.Protocol.Tests;

public class PairingPasswordTests
{
    [Fact]
    public void Hash_UsesAFreshSaltAndVerifiesOnlyThatPassword()
    {
        var first = PairingPassword.Hash("correct horse");
        var second = PairingPassword.Hash("correct horse");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.Equal(PairingPassword.Iterations, first.Iterations);
        Assert.True(first.Iterations >= 100_000);
        Assert.Equal(PairingPassword.SaltBytes, Convert.FromBase64String(first.Salt).Length);
        Assert.Equal(PairingPassword.HashBytes, Convert.FromBase64String(first.Hash).Length);
        Assert.True(PairingPassword.Verify("correct horse", first));
        Assert.True(PairingPassword.Verify("correct horse", second));
        Assert.False(PairingPassword.Verify("wrong horse", first));
        Assert.False(PairingPassword.Verify("", first));
        Assert.False(PairingPassword.Verify(null, first));
    }

    [Fact]
    public void Ensure_SeedsWhenMissingAndKeepsAStoredHash()
    {
        var seeded = PairingPassword.Ensure(null, 0, null);
        Assert.True(PairingPassword.Verify(PairingPassword.DefaultPassword, seeded));
        Assert.NotEqual(PairingPassword.DefaultPassword, seeded.Salt);
        Assert.NotEqual(PairingPassword.DefaultPassword, seeded.Hash);
        Assert.Equal(seeded, PairingPassword.Ensure(seeded.Salt, seeded.Iterations, seeded.Hash));

        var custom = PairingPassword.Hash("other-password");
        var kept = PairingPassword.Ensure(custom.Salt, custom.Iterations, custom.Hash);
        Assert.Equal(custom, kept);
        Assert.False(PairingPassword.Verify(PairingPassword.DefaultPassword, kept));
        Assert.True(PairingPassword.Verify("other-password", kept));
    }

    [Fact]
    public void SeedDefault_DoesNotStoreThePassword()
    {
        var first = PairingPassword.SeedDefault();
        var second = PairingPassword.SeedDefault();
        Assert.True(PairingPassword.Verify(PairingPassword.DefaultPassword, first));
        Assert.False(PairingPassword.Verify("not-the-default", first));
        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.DoesNotContain(PairingPassword.DefaultPassword, first.Salt, StringComparison.Ordinal);
        Assert.DoesNotContain(PairingPassword.DefaultPassword, first.Hash, StringComparison.Ordinal);
    }

    [Fact]
    public void HashesMatch_ComparesTheWholeBuffer()
    {
        var same = new byte[PairingPassword.HashBytes];
        var firstByte = (byte[])same.Clone();
        firstByte[0] = 1;
        var lastByte = (byte[])same.Clone();
        lastByte[^1] = 1;

        Assert.True(PairingPassword.HashesMatch(same, same));
        Assert.False(PairingPassword.HashesMatch(same, firstByte));
        Assert.False(PairingPassword.HashesMatch(same, lastByte));
        Assert.False(PairingPassword.HashesMatch(same, new byte[8]));
        Assert.False(PairingPassword.HashesMatch(same, []));
        Assert.False(PairingPassword.HashesMatch([], []));
    }

    [Fact]
    public void Verify_RejectsAFlippedOrTruncatedHash()
    {
        var stored = PairingPassword.Hash("sample-password");
        var bytes = Convert.FromBase64String(stored.Hash);
        bytes[0] ^= 0x01;
        var flipped = stored with { Hash = Convert.ToBase64String(bytes) };
        var truncated = stored with { Hash = Convert.ToBase64String(new byte[8]) };

        Assert.True(PairingPassword.Verify("sample-password", stored));
        Assert.False(PairingPassword.Verify("sample-password", flipped));
        Assert.False(PairingPassword.Verify("sample-password", truncated));
        Assert.False(PairingPassword.Verify("sample-password", new PairingPasswordRecord("", 0, "")));
        Assert.False(PairingPassword.Verify("sample-password", stored with { Iterations = 1 }));
    }

    [Fact]
    public void PublishedDocs_DoNotContainTheDefaultPassword()
    {
        var root = RepoRoot();
        var hits = new List<string>();
        foreach (var path in PublishedDocs(root))
        {
            var text = File.ReadAllText(path);
            if (text.Contains(PairingPassword.DefaultPassword, StringComparison.OrdinalIgnoreCase))
                hits.Add(Path.GetRelativePath(root, path));
        }

        Assert.Empty(hits);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HotSonos.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }

    static IEnumerable<string> PublishedDocs(string root)
    {
        yield return Path.Combine(root, "README.md");
        yield return Path.Combine(root, "AGENTS.md");
        yield return Path.Combine(root, "spec.md");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.*", SearchOption.AllDirectories))
            yield return path;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "site"), "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path);
            if (ext is ".html" or ".py" or ".css" or ".md" or ".txt" or ".xml" or ".webmanifest")
                yield return path;
        }
    }
}
