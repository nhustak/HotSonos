using System.Diagnostics;
using System.IO;
using System.Text;

namespace HotSonos.App.Infrastructure;

/// <summary>
/// Lightweight diagnostics for the tray app: rolling daily files under
/// %LocalAppData%\HotSonos\logs plus an in-memory ring for "Copy diagnostics".
/// Never throws to callers — logging must not take down the app.
/// </summary>
public static class AppLog
{
    private const int RingCapacity = 500;
    private const int RetainDays = 7;

    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new(RingCapacity);
    private static bool _pruned;

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotSonos", "logs");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>Recent ring lines (newest last), for clipboard / support dumps.</summary>
    public static string GetRecentText(int maxLines = 200)
    {
        lock (Gate)
        {
            var take = Math.Min(maxLines, Ring.Count);
            if (take == 0)
                return $"(no log lines yet; directory: {DirectoryPath}){Environment.NewLine}";

            return string.Join(Environment.NewLine, Ring.TakeLast(take)) + Environment.NewLine;
        }
    }

    /// <summary>Opens the log folder in Explorer (creates it if missing).</summary>
    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = DirectoryPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppLog.OpenLogFolder failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies recent log text to the clipboard. Returns false if the clipboard
    /// is unavailable (e.g. called off the UI thread without STA).
    /// </summary>
    public static bool TryCopyRecentToClipboard(int maxLines = 200)
    {
        try
        {
            var text = GetRecentText(maxLines);
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Error("Could not copy diagnostics to clipboard", ex);
            return false;
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = ex is null
                ? $"{stamp} [{level}] {message}"
                : $"{stamp} [{level}] {message}: {ex.GetType().Name}: {ex.Message}";

            // Include a one-line stack for errors (trimmed).
            if (ex is not null && level == "ERROR" && ex.StackTrace is { } stack)
            {
                var firstFrame = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(firstFrame))
                    line += $" | {firstFrame}";
            }

            lock (Gate)
            {
                if (Ring.Count >= RingCapacity)
                    Ring.Dequeue();
                Ring.Enqueue(line);

                Directory.CreateDirectory(DirectoryPath);
                if (!_pruned)
                {
                    PruneOldFilesUnlocked();
                    _pruned = true;
                }

                var file = Path.Combine(DirectoryPath, $"hotsonos-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }

            Debug.WriteLine(line);
        }
        catch
        {
            // Logging must never throw.
        }
    }

    private static void PruneOldFilesUnlocked()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-RetainDays);
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "hotsonos-*.log"))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(path); // hotsonos-yyyyMMdd
                    var datePart = name.Length >= 8 ? name[^8..] : null;
                    if (datePart is not null &&
                        DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                            System.Globalization.DateTimeStyles.None, out var day) &&
                        day < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Skip unreadable/locked files.
                }
            }
        }
        catch
        {
            // Best-effort prune.
        }
    }
}
