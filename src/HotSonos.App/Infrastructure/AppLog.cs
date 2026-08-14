using System.Diagnostics;
using System.IO;
using System.Text;

namespace HotSonos.App.Infrastructure;

/// <summary>
/// Lightweight diagnostics for the tray app: rolling daily files under
/// %LocalAppData%\HotSonos\logs plus an in-memory ring for "Copy diagnostics".
/// Never throws to callers ΓÇö logging must not take down the app.
/// Disk: one active file per day, rotated when it exceeds <see cref="MaxDailyFileBytes"/>;
/// files older than <see cref="RetainDays"/> are pruned. UI must use the ring only.
/// </summary>
public static class AppLog
{
    private const int RingCapacity = 500;
    private const int RetainDays = 7;

    /// <summary>Rotate the active day file when it reaches this size (8 MB).</summary>
    public const long MaxDailyFileBytes = 8L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new(RingCapacity);
    private static bool _pruned;

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotSonos", "logs");

    /// <summary>Active day log path (may not exist yet).</summary>
    public static string TodayLogPath =>
        Path.Combine(DirectoryPath, $"hotsonos-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Short status for the Logs tab path line (size + caps).</summary>
    public static string DescribeLogStorage()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var today = TodayLogPath;
            var sizeNote = File.Exists(today)
                ? $"{FormatBytes(new FileInfo(today).Length)} active"
                : "no file yet";
            long total = 0;
            var count = 0;
            foreach (var f in Directory.EnumerateFiles(DirectoryPath, "hotsonos-*.log"))
            {
                try
                {
                    total += new FileInfo(f).Length;
                    count++;
                }
                catch
                {
                    /* skip */
                }
            }

            return $"In-memory ring (last {RingCapacity}) ┬╖ today: {sizeNote} ┬╖ " +
                   $"{count} file(s) {FormatBytes(total)} ┬╖ rotate at {FormatBytes(MaxDailyFileBytes)} ┬╖ " +
                   $"keep {RetainDays}d ┬╖ {DirectoryPath}";
        }
        catch (Exception ex)
        {
            return $"Log folder: {DirectoryPath} ({ex.Message})";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>
    /// Absolute path of the last "about to do X" breadcrumb (single-line overwrite, flushed).
    /// After a hard death, this is usually more useful than the daily log tail.
    /// </summary>
    public static string LastActionPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotSonos", "last-action.txt");

    /// <summary>
    /// <b>Before</b> a risky/UI/network step: daily log + immediate flush to
    /// <see cref="LastActionPath"/> so a hard kill leaves "what we were about to do".
    /// Keep messages short. Call immediately before the work, not after.
    /// </summary>
    public static void Before(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return;

        var note = action.Trim();
        if (note.Length > 240)
            note = note[..237] + "...";

        Write("PRE", note, null);
        WriteLastActionFlushed(note);
    }

    /// <summary>
    /// Process start / exit / fatal milestones only (not heartbeats).
    /// Goes to the daily log <b>and</b> a small capped chronological trail
    /// (<see cref="LifecycleTrailPath"/>) so death reasons stay in order without
    /// a separate overwriting "last-exit" file that lied about exits.
    /// </summary>
    public static void Lifecycle(string message)
    {
        Write("LIFE", message, null);
        AppendLifecycleTrail(message);
        WriteLastActionFlushed("LIFE: " + message);
    }

    /// <summary>
    /// Heartbeat / liveness: daily log + ring only. Never touches the lifecycle trail
    /// (heartbeats used to overwrite last-exit and hide real exits).
    /// </summary>
    public static void Heartbeat(string message) => Write("LIFE", message, null);

    private static readonly object LastActionGate = new();

    /// <summary>Overwrite last-action.txt with WriteThrough so it survives hard death.</summary>
    private static void WriteLastActionFlushed(string note)
    {
        try
        {
            var dir = Path.GetDirectoryName(LastActionPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | pid={Environment.ProcessId} | {note}"
                + Environment.NewLine;

            // Serialize multi-thread breadcrumbs (UI + timer + GENA) ΓÇö concurrent Create was racy.
            lock (LastActionGate)
            {
                using var fs = new FileStream(
                    LastActionPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 512,
                    FileOptions.WriteThrough);
                var bytes = Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
        }
        catch
        {
            /* never throw from diagnostics */
        }
    }

    /// <summary>
    /// Small append-only lifecycle trail under %LocalAppData%\HotSonos\lifecycle.log
    /// (capped; not the chatty daily log). Chronological ΓÇö never single-line overwrite.
    /// </summary>
    public static string LifecycleTrailPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotSonos", "lifecycle.log");

    /// <summary>Max size of <see cref="LifecycleTrailPath"/> before trim (64 KB).</summary>
    public const int MaxLifecycleTrailBytes = 64 * 1024;

    /// <summary>Bytes kept after trim (~48 KB of newest lines).</summary>
    public const int KeepLifecycleTrailBytes = 48 * 1024;

    /// <summary>Recent lifecycle trail text (newest last). Empty if none.</summary>
    public static string GetLifecycleTrailText(int maxLines = 80)
    {
        maxLines = Math.Clamp(maxLines, 1, 500);
        try
        {
            if (!File.Exists(LifecycleTrailPath))
                return "(no lifecycle trail yet)\r\n";

            var lines = File.ReadAllLines(LifecycleTrailPath, Encoding.UTF8);
            if (lines.Length == 0)
                return "(lifecycle trail empty)\r\n";
            var take = Math.Min(maxLines, lines.Length);
            var start = lines.Length - take;
            return string.Join(Environment.NewLine, lines.Skip(start))
                   + Environment.NewLine;
        }
        catch (Exception ex)
        {
            return $"(lifecycle trail read failed: {ex.Message}){Environment.NewLine}";
        }
    }

    /// <summary>
    /// On boot: if the previous session never logged a clean exit, note that in the trail.
    /// Call once near process start (before the new "Starting" line is ideal, or right after).
    /// </summary>
    public static void NoteUncleanPriorExitIfAny()
    {
        try
        {
            if (!File.Exists(LifecycleTrailPath))
                return;

            var lines = File.ReadAllLines(LifecycleTrailPath, Encoding.UTF8);
            if (lines.Length == 0)
                return;

            // Walk from end: skip blank; find last substantive line.
            string? last = null;
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    last = lines[i].Trim();
                    break;
                }
            }

            if (last is null)
                return;

            if (LooksLikeCleanExit(last))
                return;

            // Prior run started or mid-life then vanished ΓÇö say so before we log Starting.
            AppendLifecycleTrailOnly(
                $"NOTE prior session likely unclean exit (last trail line was not a clean stop): {last}");
            Write("WARN",
                "Prior HotSonos session likely died without clean exit ΓÇö see lifecycle.log",
                null);
        }
        catch
        {
            /* ignore */
        }
    }

    private static bool LooksLikeCleanExit(string line) =>
        line.Contains("Exit requested", StringComparison.OrdinalIgnoreCase)
        || line.Contains("ProcessExit", StringComparison.OrdinalIgnoreCase)
        || line.Contains("OnExit ", StringComparison.OrdinalIgnoreCase)
        || line.Contains("WPF Exit", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Second instance exit", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Calling Application.Shutdown", StringComparison.OrdinalIgnoreCase);

    private static void AppendLifecycleTrail(string message) =>
        AppendLifecycleTrailOnly(message);

    private static void AppendLifecycleTrailOnly(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LifecycleTrailPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line =
                $"{stamp} | pid={Environment.ProcessId} exitCode={Environment.ExitCode} | {message}"
                + Environment.NewLine;

            // Append + flush so a hard kill still often leaves the last milestone.
            using (var fs = new FileStream(
                       LifecycleTrailPath,
                       FileMode.Append,
                       FileAccess.Write,
                       FileShare.ReadWrite,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var sw = new StreamWriter(fs, Encoding.UTF8))
            {
                sw.Write(line);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

            TrimLifecycleTrailIfNeeded();

            // Retire the old single-overwrite file if present (it was actively misleading).
            try
            {
                var legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HotSonos", "last-exit.txt");
                if (File.Exists(legacy))
                    File.Delete(legacy);
            }
            catch
            {
                /* ignore */
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private static void TrimLifecycleTrailIfNeeded()
    {
        try
        {
            var info = new FileInfo(LifecycleTrailPath);
            if (!info.Exists || info.Length <= MaxLifecycleTrailBytes)
                return;

            // Keep the newest ~KeepLifecycleTrailBytes as whole lines.
            using var fs = new FileStream(
                LifecycleTrailPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var keep = (int)Math.Min(KeepLifecycleTrailBytes, fs.Length);
            fs.Seek(-keep, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var tail = reader.ReadToEnd();
            var nl = tail.IndexOf('\n');
            if (nl >= 0 && nl + 1 < tail.Length)
                tail = tail[(nl + 1)..];

            var trimmed =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | (lifecycle trail trimmed to last ~{FormatBytes(KeepLifecycleTrailBytes)})"
                + Environment.NewLine
                + tail;

            File.WriteAllText(LifecycleTrailPath, trimmed, Encoding.UTF8);
        }
        catch
        {
            /* ignore */
        }
    }

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

            string? pathToWrite = null;
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

                // Do not hold the lock while writing disk ΓÇö dual-write spam was
                // serializing the app (including volume hotkeys).
                pathToWrite = ResolveActiveLogPathUnlocked();
            }

            if (pathToWrite is not null)
            {
                try
                {
                    File.AppendAllText(pathToWrite, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    /* ignore disk errors */
                }
            }

            Debug.WriteLine(line);
        }
        catch
        {
            // Logging must never throw.
        }
    }

    /// <summary>
    /// Active day log path; if the current file is at/over the size cap, rename it
    /// to a part file and start a fresh active file so one file never grows without bound.
    /// </summary>
    private static string ResolveActiveLogPathUnlocked()
    {
        var day = DateTime.Now.ToString("yyyyMMdd");
        var primary = Path.Combine(DirectoryPath, $"hotsonos-{day}.log");
        try
        {
            if (!File.Exists(primary))
                return primary;

            var len = new FileInfo(primary).Length;
            if (len < MaxDailyFileBytes)
                return primary;

            for (var part = 1; part < 200; part++)
            {
                var rotated = Path.Combine(DirectoryPath, $"hotsonos-{day}.{part}.log");
                if (File.Exists(rotated))
                    continue;

                File.Move(primary, rotated);
                // Leave a one-line marker in the new active file (written by caller via Append).
                // Pre-seed so diagnostics show why the previous chunk ended.
                try
                {
                    File.WriteAllText(
                        primary,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [LIFE] Log rotated ΓåÆ {Path.GetFileName(rotated)} " +
                        $"(reached {FormatBytes(len)}; cap {FormatBytes(MaxDailyFileBytes)}){Environment.NewLine}",
                        Encoding.UTF8);
                }
                catch
                {
                    /* ignore */
                }

                return primary;
            }
        }
        catch
        {
            // Fall through ΓÇö still try primary path.
        }

        return primary;
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
                    // Names: hotsonos-yyyyMMdd.log or hotsonos-yyyyMMdd.N.log
                    var name = Path.GetFileName(path);
                    if (name.Length < 17 || !name.StartsWith("hotsonos-", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var datePart = name.AsSpan(9, 8);
                    if (DateTime.TryParseExact(datePart, "yyyyMMdd", null,
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
