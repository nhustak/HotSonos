using HotSonos.App.Services;

namespace HotSonos.App.Tests;

public class PhoneWirelessDebugTests
{
    [Theory]
    [InlineData("192.168.1.171", "192.168.1.171")]
    [InlineData("  10.0.0.8  ", "10.0.0.8")]
    [InlineData("pixel.local", "pixel.local")]
    public void Host_AcceptsTrimmedAddress(string input, string expected)
    {
        Assert.True(PhoneWirelessDebug.TryValidateHost(input, out var host, out var error));
        Assert.Equal(expected, host);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Host_RejectsEmpty(string? input)
    {
        Assert.False(PhoneWirelessDebug.TryValidateHost(input, out var host, out var error));
        Assert.Equal("", host);
        Assert.Equal(PhoneWirelessDebug.HostRequiredError, error);
    }

    [Theory]
    [InlineData("192.168.1.171:34011")]
    [InlineData("192.168.1.171 34011")]
    public void Host_RejectsSpacesAndColon(string input)
    {
        Assert.False(PhoneWirelessDebug.TryValidateHost(input, out _, out var error));
        Assert.Equal(PhoneWirelessDebug.HostInvalidError, error);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("34011", 34011)]
    [InlineData(" 34011 ", 34011)]
    public void Port_Accepts1Through65535(string input, int expected)
    {
        Assert.True(PhoneWirelessDebug.TryValidatePort(input, out var port, out var error));
        Assert.Equal(expected, port);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("34.011")]
    [InlineData("abc")]
    [InlineData("+34011")]
    public void Port_RejectsOutOfRange(string? input)
    {
        Assert.False(PhoneWirelessDebug.TryValidatePort(input, out var port, out var error));
        Assert.Equal(0, port);
        Assert.Equal(PhoneWirelessDebug.PortError, error);
    }

    [Theory]
    [InlineData("246810")]
    [InlineData(" 246810 ")]
    [InlineData("000000")]
    public void PairingCode_AcceptsSixDigits(string input)
    {
        Assert.True(PhoneWirelessDebug.TryValidatePairingCode(input, out var code, out var error));
        Assert.Equal(input.Trim(), code);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("12 456")]
    public void PairingCode_RejectsAnythingElse(string? input)
    {
        Assert.False(PhoneWirelessDebug.TryValidatePairingCode(input, out var code, out var error));
        Assert.Equal("", code);
        Assert.Equal(PhoneWirelessDebug.CodeError, error);
    }

    [Fact]
    public void Normalize_UsesDefaultsForEmptyHostAndOutOfRangePort()
    {
        Assert.Equal(PhoneWirelessDebug.DefaultHost, PhoneWirelessDebug.NormalizeHost(null));
        Assert.Equal(PhoneWirelessDebug.DefaultHost, PhoneWirelessDebug.NormalizeHost("  "));
        Assert.Equal("192.168.1.171", PhoneWirelessDebug.DefaultHost);
        Assert.Equal(34011, PhoneWirelessDebug.DefaultConnectPort);
        Assert.Equal(34011, PhoneWirelessDebug.NormalizePort(0));
        Assert.Equal(34011, PhoneWirelessDebug.NormalizePort(65536));
        Assert.Equal(1, PhoneWirelessDebug.NormalizePort(1));
        Assert.Equal(65535, PhoneWirelessDebug.NormalizePort(65535));
        Assert.Equal("10.1.2.3", PhoneWirelessDebug.NormalizeHost(" 10.1.2.3 "));
    }

    [Fact]
    public void EndpointFile_IsHostColonPortAndNothingElse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-phoneadb-" + Guid.NewGuid().ToString("n"));
        try
        {
            PhoneWirelessDebug.WriteEndpointFile(dir, " 192.168.1.50 ", 37123);
            var path = Path.Combine(dir, PhoneWirelessDebug.EndpointFileName);
            Assert.Equal("phone-adb.txt", PhoneWirelessDebug.EndpointFileName);
            Assert.Equal("192.168.1.50:37123", File.ReadAllText(path));
            Assert.DoesNotContain("246810", File.ReadAllText(path));

            PhoneWirelessDebug.WriteEndpointFile(dir, " ", 0);
            Assert.Equal("192.168.1.171:34011", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Commands_PassTheCodeToAdbAndHideItFromTheDisplay()
    {
        const string code = "246810";
        var args = PhoneWirelessDebug.PairArguments("192.168.1.171", 37123, code);
        Assert.Equal(new[] { "pair", "192.168.1.171:37123", code }, args);
        var display = PhoneWirelessDebug.PairDisplay("192.168.1.171", 37123);
        Assert.Equal("adb pair 192.168.1.171:37123 ******", display);
        Assert.DoesNotContain(code, display);
        Assert.Equal(
            "adb pair 192.168.1.171:37123 ******",
            PhoneWirelessDebug.Redact("adb pair 192.168.1.171:37123 " + code, code));
        Assert.Equal(new[] { "connect", "192.168.1.171:34011" }, PhoneWirelessDebug.ConnectArguments("192.168.1.171", 34011));
        Assert.Equal("adb connect 192.168.1.171:34011", PhoneWirelessDebug.ConnectDisplay("192.168.1.171", 34011));
    }

    [Fact]
    public async Task Execute_RedactsTheCodeFromResultAndLog()
    {
        const string code = "246810";
        var logs = new List<string>();
        IReadOnlyList<string>? seen = null;
        var result = await PhoneWirelessDebug.ExecuteAsync(
            "adb.exe",
            PhoneWirelessDebug.PairArguments("192.168.1.171", 37123, code),
            PhoneWirelessDebug.PairDisplay("192.168.1.171", 37123),
            code,
            logs.Add,
            (_, args, _) =>
            {
                seen = args.ToArray();
                var echoed = "Successfully paired to " + string.Join(' ', args);
                return Task.FromResult(new PhoneAdbProcessResult(0, echoed, "code " + code));
            });

        Assert.True(result.Ok);
        Assert.NotNull(seen);
        Assert.Equal(code, seen![2]);
        Assert.DoesNotContain(code, result.Text);
        Assert.Contains("adb pair 192.168.1.171:37123 ******", result.Text);
        Assert.Contains("Successfully paired to pair 192.168.1.171:37123 ******", result.Text);
        Assert.Contains("exit 0", result.Text);
        Assert.All(logs, line => Assert.DoesNotContain(code, line));
    }

    [Fact]
    public async Task Execute_RedactsTheCodeWhenAdbThrows()
    {
        const string code = "135790";
        var logs = new List<string>();
        var result = await PhoneWirelessDebug.ExecuteAsync(
            "adb.exe",
            PhoneWirelessDebug.PairArguments("192.168.1.171", 1, code),
            "adb pair 192.168.1.171:1 " + code,
            code,
            logs.Add,
            (_, args, _) => throw new InvalidOperationException("adb failed with " + args[2]));

        Assert.False(result.Ok);
        Assert.DoesNotContain(code, result.Text);
        Assert.Contains("******", result.Text);
        Assert.All(logs, line => Assert.DoesNotContain(code, line));
    }

    [Fact]
    public async Task Execute_TimesOutWithoutLoggingTheCode()
    {
        const string code = "135790";
        var logs = new List<string>();
        var result = await PhoneWirelessDebug.ExecuteAsync(
            "adb.exe",
            PhoneWirelessDebug.PairArguments("192.168.1.171", 1, code),
            PhoneWirelessDebug.PairDisplay("192.168.1.171", 1),
            code,
            logs.Add,
            async (_, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new PhoneAdbProcessResult(0, code, "");
            },
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.False(result.Ok);
        Assert.Contains("adb timed out.", result.Text);
        Assert.DoesNotContain(code, result.Text);
        Assert.All(logs, line => Assert.DoesNotContain(code, line));
    }

    [Fact]
    public void ResolveAdb_PrefersSdkPathThenPath()
    {
        var local = Path.Combine(Path.GetTempPath(), "local-" + Guid.NewGuid().ToString("n"));
        var pathDir = Path.Combine(Path.GetTempPath(), "path-" + Guid.NewGuid().ToString("n"));
        var sdk = PhoneWirelessDebug.SdkAdbPath(local);
        var pathExe = Path.Combine(pathDir, "adb.exe");
        var plain = Path.Combine(pathDir, "adb");
        var path = "\"" + pathDir + "\"";

        Assert.Equal(sdk, PhoneWirelessDebug.ResolveAdb(local, path, p => p == sdk || p == pathExe));
        Assert.Equal(pathExe, PhoneWirelessDebug.ResolveAdb(local, path, p => p == pathExe));
        Assert.Equal(plain, PhoneWirelessDebug.ResolveAdb(local, pathDir, p => p == plain));
        Assert.Null(PhoneWirelessDebug.ResolveAdb(local, path, _ => false));
        Assert.Null(PhoneWirelessDebug.ResolveAdb(local, null, _ => false));
        Assert.Contains("adb.exe", PhoneWirelessDebug.MissingAdbMessage);
        Assert.Contains("PATH", PhoneWirelessDebug.MissingAdbMessage);
    }
}
