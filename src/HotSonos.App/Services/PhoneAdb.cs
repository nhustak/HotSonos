using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace HotSonos.App.Services;

/// <summary>
/// Phone wireless-debugging address for <c>adb connect</c>.
/// Persisted on <c>AppSettings</c> and mirrored to <c>phone-adb.txt</c>.
/// </summary>
public static class PhoneAdb
{
    public const string DefaultHost = "192.168.1.171";
    public const int DefaultPort = 34011;
    public const string AddressFileName = "phone-adb.txt";

    public const string EmptyHostError = "Address is empty. Enter the phone's wireless debugging IP or hostname.";
    public const string BadHostError = "Address must be an IP address or hostname, without a port.";
    public const string BadPortError = "Port must be a number from 1 to 65535.";

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Replace a missing or invalid host or port with the defaults. Valid parts are kept.</summary>
    public static (string Host, int Port) Coerce(string? host, int port)
    {
        if (TryNormalize(host, port, out var normalizedHost, out var normalizedPort, out _))
            return (normalizedHost, normalizedPort);

        var hostOk = TryNormalize(host, DefaultPort, out normalizedHost, out _, out _);
        return (hostOk ? normalizedHost : DefaultHost, port is >= 1 and <= 65535 ? port : DefaultPort);
    }

    /// <summary>True when <paramref name="host"/> is a non-empty IP or hostname and <paramref name="port"/> is 1–65535.</summary>
    public static bool TryNormalize(string? host, int port, out string normalizedHost, out int normalizedPort, out string? error)
    {
        normalizedHost = "";
        normalizedPort = 0;
        var trimmed = (host ?? "").Trim();
        if (trimmed.Length == 0)
        {
            error = EmptyHostError;
            return false;
        }

        if (!IsHostToken(trimmed))
        {
            error = BadHostError;
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = BadPortError;
            return false;
        }

        normalizedHost = trimmed;
        normalizedPort = port;
        error = null;
        return true;
    }

    /// <summary>Parse the Phone window fields. The port must be an integer with no extra text.</summary>
    public static bool TryParse(string? hostText, string? portText, out string host, out int port, out string? error)
    {
        host = "";
        port = 0;
        var portTrimmed = (portText ?? "").Trim();
        if (portTrimmed.Length == 0
            || !int.TryParse(portTrimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            error = BadPortError;
            return false;
        }

        return TryNormalize(hostText, parsed, out host, out port, out error);
    }

    public static string FormatEndpoint(string host, int port) => $"{host}:{port}";

    public static string AddressFilePath(string directory) =>
        Path.Combine(directory, AddressFileName);

    /// <summary>Write <c>host:port</c> as a single line. Returns false and writes nothing when the address is invalid.</summary>
    public static bool TryWriteAddressFile(string directory, string? host, int port, out string? error)
    {
        if (!TryNormalize(host, port, out var normalizedHost, out var normalizedPort, out error))
            return false;

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(AddressFilePath(directory), FormatEndpoint(normalizedHost, normalizedPort) + "\n");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string SdkAdbPath(string localAppData) =>
        Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");

    public static string NotFoundMessage(string? localAppData = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return $"adb was not found at {SdkAdbPath(localAppData)}, or as adb on PATH.";
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe</c> when that file exists,
    /// otherwise <c>adb.exe</c> or <c>adb</c> on <paramref name="pathVariable"/>.
    /// </summary>
    public static string? FindAdb(string? localAppData = null, string? pathVariable = null, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var preferred = SdkAdbPath(localAppData);
        if (fileExists(preferred))
            return preferred;

        pathVariable ??= Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
            return null;

        foreach (var raw in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0)
                continue;
            foreach (var name in new[] { "adb.exe", "adb" })
            {
                var candidate = Path.Combine(dir, name);
                if (fileExists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public static ProcessStartInfo CreateStartInfo(string adbPath, string host, int port)
    {
        var start = new ProcessStartInfo
        {
            FileName = adbPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("connect");
        start.ArgumentList.Add(FormatEndpoint(host, port));
        return start;
    }

    /// <summary>Run <c>adb connect host:port</c>. Call this off the UI thread.</summary>
    public static async Task<PhoneAdbConnectResult> ConnectAsync(
        string adbPath,
        string host,
        int port,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(adbPath))
            return PhoneAdbConnectResult.Fail(NotFoundMessage());
        if (!TryNormalize(host, port, out host, out port, out var error))
            return PhoneAdbConnectResult.Fail(error!);

        var start = CreateStartInfo(adbPath, host, port);
        var limit = timeout ?? DefaultConnectTimeout;
        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start())
                return PhoneAdbConnectResult.Fail("Could not start adb.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(limit);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await Drain(stdoutTask, stderrTask).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return PhoneAdbConnectResult.Fail("adb connect timed out.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return Interpret(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PhoneAdbConnectResult.Fail(ex.Message);
        }
    }

    public static PhoneAdbConnectResult Interpret(int exitCode, string? stdout, string? stderr)
    {
        var text = string.Join(
            Environment.NewLine,
            new[] { stdout, stderr }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
        if (text.Length == 0)
            text = exitCode == 0 ? "adb connect finished." : $"adb exited with code {exitCode}.";

        var failed = exitCode != 0
            || Contains(text, "failed")
            || Contains(text, "cannot connect")
            || Contains(text, "unable to connect");
        var connected = Contains(text, "connected");
        if (!failed && (connected || exitCode == 0))
            return PhoneAdbConnectResult.Ok(text);
        return PhoneAdbConnectResult.Fail(text);
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
    }

    private static async Task Drain(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // The pipes close when adb exits. Their text is unused after a timeout.
        }
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool IsHostToken(string host)
    {
        if (host.Length is < 1 or > 253)
            return false;
        if (host[0] is '-' or '.' || host[^1] is '-' or '.')
            return false;
        if (host.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var c in host)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-')
                continue;
            return false;
        }

        return true;
    }
}

public sealed class PhoneAdbConnectResult
{
    private PhoneAdbConnectResult(bool connected, string message)
    {
        Connected = connected;
        Message = message;
        var marker = connected ? "connected" : "failed";
        DisplayText = message.Contains(marker, StringComparison.OrdinalIgnoreCase)
            ? message
            : (connected ? "Connected. " : "Failed. ") + message;
    }

    public bool Connected { get; }
    public string Message { get; }

    /// <summary>Text for the Phone window. Always includes "connected" or "failed".</summary>
    public string DisplayText { get; }

    public static PhoneAdbConnectResult Ok(string message) => new(true, message);
    public static PhoneAdbConnectResult Fail(string message) => new(false, message);
}
