using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace HotSonos.App.Services;

/// <summary>
/// Wireless adb for the phone app. The pairing code is never stored and never written to a log line.
/// Host and connect port live on <see cref="Models.AppSettings"/> (<c>PhoneAdbHost</c>, <c>PhoneAdbPort</c>).
/// </summary>
public static class PhoneWirelessDebug
{
    public const string DefaultHost = "192.168.1.171";
    public const int DefaultConnectPort = 34011;
    public const string EndpointFileName = "phone-adb.txt";

    public const string HostRequiredError = "Phone adb IP is required.";
    public const string HostInvalidError = "Phone adb IP cannot include spaces or a colon.";
    public const string PortError = "Port must be a number from 1 to 65535.";
    public const string CodeError = "Pairing code must be 6 digits.";

    public const string MissingAdbMessage =
        "adb.exe was not found at %LOCALAPPDATA%\\Android\\Sdk\\platform-tools\\adb.exe, and adb is not on PATH.";

    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    public static string SdkAdbPath(string localAppData) =>
        Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");

    public static bool TryValidateHost(string? host, out string normalized, out string? error)
    {
        normalized = (host ?? "").Trim();
        if (normalized.Length == 0)
        {
            error = HostRequiredError;
            return false;
        }

        if (normalized.Any(c => char.IsWhiteSpace(c) || c == ':'))
        {
            normalized = "";
            error = HostInvalidError;
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidatePort(string? text, out int port, out string? error)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0
            || !int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            port = 0;
            error = PortError;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>One-time wireless-debugging code. The normalized value is empty when validation fails.</summary>
    public static bool TryValidatePairingCode(string? code, out string normalized, out string? error)
    {
        normalized = (code ?? "").Trim();
        if (normalized.Length == 6 && normalized.All(char.IsDigit))
        {
            error = null;
            return true;
        }

        normalized = "";
        error = CodeError;
        return false;
    }

    /// <summary>Empty or invalid hosts become <see cref="DefaultHost"/>.</summary>
    public static string NormalizeHost(string? host) =>
        TryValidateHost(host, out var normalized, out _) ? normalized : DefaultHost;

    /// <summary>Ports outside 1–65535 become <see cref="DefaultConnectPort"/>.</summary>
    public static int NormalizePort(int port) =>
        port is >= 1 and <= 65535 ? port : DefaultConnectPort;

    public static string Endpoint(string host, int port) => $"{host}:{port}";

    /// <summary>Writes exactly <c>host:port</c> for deploy scripts. No pairing code.</summary>
    public static void WriteEndpointFile(string directory, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Phone adb settings folder is required.", nameof(directory));

        host = NormalizeHost(host);
        port = NormalizePort(port);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, EndpointFileName), Endpoint(host, port));
    }

    public static string PairDisplay(string host, int pairPort) =>
        $"adb pair {host}:{pairPort} ******";

    public static string ConnectDisplay(string host, int port) =>
        $"adb connect {host}:{port}";

    public static string[] PairArguments(string host, int pairPort, string code) =>
        ["pair", $"{host}:{pairPort}", code];

    public static string[] ConnectArguments(string host, int port) =>
        ["connect", $"{host}:{port}"];

    /// <summary>Replaces <paramref name="secret"/> so a command line or adb echo can be shown and logged.</summary>
    public static string Redact(string? text, string? secret)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (string.IsNullOrEmpty(secret))
            return text;
        return text.Replace(secret, "******", StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe</c>, then <c>adb.exe</c> / <c>adb</c> on PATH.
    /// </summary>
    public static string? ResolveAdb(string localAppData, string? pathVariable, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var bundled = SdkAdbPath(localAppData);
        if (exists(bundled))
            return bundled;

        if (string.IsNullOrWhiteSpace(pathVariable))
            return null;

        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;

            var exe = Path.Combine(trimmed, "adb.exe");
            if (exists(exe))
                return exe;

            var plain = Path.Combine(trimmed, "adb");
            if (exists(plain))
                return plain;
        }

        return null;
    }

    public static string? ResolveAdb() =>
        ResolveAdb(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH"));

    public static async Task<PhoneAdbExecution> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string display,
        string? secret,
        Action<string>? log,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<PhoneAdbProcessResult>>? runner = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var shown = Redact(display, secret);
        runner ??= RunProcessAsync;
        using var timeoutCts = new CancellationTokenSource(timeout ?? CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            var processResult = await runner(fileName, arguments, linked.Token).ConfigureAwait(false);
            var body = Redact(Combine(processResult.Stdout, processResult.Stderr), secret);
            var text = string.IsNullOrEmpty(body)
                ? $"{shown}{Environment.NewLine}exit {processResult.ExitCode}"
                : $"{shown}{Environment.NewLine}{body}{Environment.NewLine}exit {processResult.ExitCode}";
            log?.Invoke(text);
            return new PhoneAdbExecution(processResult.ExitCode == 0, text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var text = shown + Environment.NewLine + "adb timed out.";
            log?.Invoke(text);
            return new PhoneAdbExecution(false, text);
        }
        catch (Exception ex)
        {
            var text = Redact(shown + Environment.NewLine + ex.Message, secret);
            log?.Invoke(text);
            return new PhoneAdbExecution(false, text);
        }
    }

    private static string Combine(string? stdout, string? stderr)
    {
        stdout = (stdout ?? "").Trim();
        stderr = (stderr ?? "").Trim();
        if (stdout.Length == 0)
            return stderr;
        if (stderr.Length == 0)
            return stdout;
        return stdout + Environment.NewLine + stderr;
    }

    private static async Task<PhoneAdbProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
                return new PhoneAdbProcessResult(-1, "", "adb did not start.");
        }
        catch (Exception ex)
        {
            return new PhoneAdbProcessResult(-1, "", ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                /* already exited */
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new PhoneAdbProcessResult(process.ExitCode, stdout, stderr);
    }
}

public readonly record struct PhoneAdbProcessResult(int ExitCode, string Stdout, string Stderr);

public readonly record struct PhoneAdbExecution(bool Ok, string Text);
