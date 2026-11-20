using HotSonos.App.Services;

namespace HotSonos.App.Tests;

public class PhoneAdbTests
{
    [Fact]
    public void TryNormalize_AcceptsIpv4AndHostname()
    {
        Assert.True(PhoneAdb.TryNormalize("  192.168.1.171  ", 34011, out var host, out var port, out var error));
        Assert.Equal("192.168.1.171", host);
        Assert.Equal(34011, port);
        Assert.Null(error);

        Assert.True(PhoneAdb.TryNormalize("pixel.lan", 1, out host, out port, out error));
        Assert.Equal("pixel.lan", host);
        Assert.Equal(1, port);

        Assert.True(PhoneAdb.TryNormalize("Phone.local", 65535, out host, out port, out error));
        Assert.Equal("Phone.local", host);
        Assert.Equal(65535, port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_RejectsEmptyHost(string? host)
    {
        Assert.False(PhoneAdb.TryNormalize(host, 34011, out _, out _, out var error));
        Assert.Equal(PhoneAdb.EmptyHostError, error);
    }

    [Theory]
    [InlineData("192.168.1.171:34011")]
    [InlineData("not a host")]
    [InlineData("host/name")]
    [InlineData(@"host\name")]
    [InlineData("-bad")]
    [InlineData("bad.")]
    [InlineData("a..b")]
    public void TryNormalize_RejectsHostThatIsNotAnAddress(string host)
    {
        Assert.False(PhoneAdb.TryNormalize(host, 34011, out _, out _, out var error));
        Assert.Equal(PhoneAdb.BadHostError, error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void TryNormalize_RejectsPortOutside1To65535(int port)
    {
        Assert.False(PhoneAdb.TryNormalize("192.168.1.171", port, out _, out _, out var error));
        Assert.Equal(PhoneAdb.BadPortError, error);
    }

    [Fact]
    public void TryParse_ReadsSeparateHostAndPortFields()
    {
        Assert.True(PhoneAdb.TryParse("192.168.1.171", " 34011 ", out var host, out var port, out var error));
        Assert.Equal("192.168.1.171", host);
        Assert.Equal(34011, port);
        Assert.Null(error);

        Assert.False(PhoneAdb.TryParse("192.168.1.171", "34011abc", out _, out _, out error));
        Assert.Equal(PhoneAdb.BadPortError, error);
        Assert.False(PhoneAdb.TryParse("192.168.1.171", "", out _, out _, out error));
        Assert.Equal(PhoneAdb.BadPortError, error);
    }

    [Fact]
    public void Coerce_ReplacesOnlyTheInvalidPart()
    {
        var (host, port) = PhoneAdb.Coerce("not a host", 5555);
        Assert.Equal(PhoneAdb.DefaultHost, host);
        Assert.Equal(5555, port);

        (host, port) = PhoneAdb.Coerce("10.1.2.3", 0);
        Assert.Equal("10.1.2.3", host);
        Assert.Equal(PhoneAdb.DefaultPort, port);

        (host, port) = PhoneAdb.Coerce(null, -4);
        Assert.Equal("192.168.1.171", host);
        Assert.Equal(34011, port);
    }

    [Fact]
    public void TryWriteAddressFile_WritesHostPortLine()
    {
        var dir = NewTempDir();
        try
        {
            Assert.True(PhoneAdb.TryWriteAddressFile(dir, " 192.168.1.171 ", 34011, out var error));
            Assert.Null(error);
            var text = File.ReadAllText(PhoneAdb.AddressFilePath(dir));
            Assert.Equal("192.168.1.171:34011", text.Trim());
            Assert.StartsWith("192.168.1.171:34011", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryWriteAddressFile_LeavesDiskUntouchedWhenInvalid()
    {
        var dir = NewTempDir();
        Directory.Delete(dir);
        try
        {
            Assert.False(PhoneAdb.TryWriteAddressFile(dir, "", 34011, out var error));
            Assert.Equal(PhoneAdb.EmptyHostError, error);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FindAdb_PrefersSdkPlatformToolsOverPath()
    {
        var local = Path.Combine("local", "app");
        var sdk = PhoneAdb.SdkAdbPath(local);
        var pathDir = Path.Combine("other");
        var onPath = Path.Combine(pathDir, "adb.exe");
        var found = PhoneAdb.FindAdb(local, pathDir, path => path == sdk || path == onPath);
        Assert.Equal(sdk, found);
    }

    [Fact]
    public void FindAdb_UsesAdbExeThenBareNameOnPath()
    {
        var pathDir = Path.Combine("tools");
        var exe = Path.Combine(pathDir, "adb.exe");
        Assert.Equal(exe, PhoneAdb.FindAdb("missing", pathDir, path => path == exe));

        var bare = Path.Combine(pathDir, "adb");
        Assert.Equal(bare, PhoneAdb.FindAdb("missing", pathDir, path => path == bare));
    }

    [Fact]
    public void FindAdb_UnquotesPathEntriesAndSkipsEmptyOnes()
    {
        var dir = Path.Combine("tools", "platform-tools");
        var exe = Path.Combine(dir, "adb.exe");
        var pathEnv = Path.PathSeparator + "\"" + dir + "\"";
        Assert.Equal(exe, PhoneAdb.FindAdb("missing", pathEnv, path => path == exe));
    }

    [Fact]
    public void FindAdb_ReturnsNullAndNamesTheSdkPathWhenMissing()
    {
        Assert.Null(PhoneAdb.FindAdb("nope", "also-nope", _ => false));
        var message = PhoneAdb.NotFoundMessage(Path.Combine("local", "app"));
        Assert.Contains(PhoneAdb.SdkAdbPath(Path.Combine("local", "app")), message);
        Assert.Contains("PATH", message);
    }

    [Theory]
    [InlineData(0, "connected to 192.168.1.171:34011\n", "", true)]
    [InlineData(0, "already connected to 192.168.1.171:34011", "", true)]
    [InlineData(1, "failed to connect to 192.168.1.171:34011: Connection refused", "", false)]
    [InlineData(0, "cannot connect to 192.168.1.171:34011", "", false)]
    [InlineData(1, "", "", false)]
    [InlineData(0, "", "", true)]
    public void Interpret_ReportsConnectedOrFailed(int exitCode, string stdout, string stderr, bool connected)
    {
        var result = PhoneAdb.Interpret(exitCode, stdout, stderr);
        Assert.Equal(connected, result.Connected);
        if (connected)
            Assert.Contains("connected", result.DisplayText, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains("failed", result.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateStartInfo_InvokesAdbConnectWithHostPort()
    {
        var adb = Path.Combine("tools", "adb.exe");
        var info = PhoneAdb.CreateStartInfo(adb, "192.168.1.171", 34011);
        Assert.Equal(adb, info.FileName);
        Assert.Equal(new[] { "connect", "192.168.1.171:34011" }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hotsonos-adb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
