using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

namespace HotSonos.App.Mcp;

/// <summary>One MCP tool invocation for the debug UI.</summary>
public sealed class McpActivityEntry
{
    public DateTime TimeLocal { get; init; }
    public string Tool { get; init; } = "";
    public string? ArgsSummary { get; init; }
    public string? ResultSummary { get; init; }
    public string? ResultFull { get; init; }
    public long DurationMs { get; init; }
    public bool Ok { get; init; }
    public string Category { get; init; } = "general"; // general | library | control | discovery

    public string HeaderLine =>
        $"{TimeLocal:HH:mm:ss.fff}  {Tool}  ({DurationMs} ms){(Ok ? "" : "  FAIL")}";

    public string DetailText
    {
        get
        {
            var args = string.IsNullOrWhiteSpace(ArgsSummary) ? "(none)" : ArgsSummary;
            var body = ResultFull ?? ResultSummary ?? "";
            return $"Tool: {Tool}\nTime: {TimeLocal:O}\nDuration: {DurationMs} ms\nOK: {Ok}\nCategory: {Category}\n\nArgs:\n{args}\n\nResult:\n{body}";
        }
    }
}

/// <summary>
/// In-process ring of MCP tool calls for the Settings → MCP Debug tab.
/// Thread-safe; raises <see cref="Changed"/> on the UI dispatcher when possible.
/// </summary>
public static class McpActivityLog
{
    public const int Capacity = 300;
    private static readonly object Gate = new();
    private static readonly List<McpActivityEntry> Entries = new(Capacity);
    private static Dispatcher? _dispatcher;

    /// <summary>Raised after a new entry is recorded (may be off UI thread).</summary>
    public static event EventHandler? Changed;

    /// <summary>Last library search payload for the Library results panel.</summary>
    public static event EventHandler<LibrarySearchPublishedEventArgs>? LibrarySearchPublished;

    public static void BindDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static IReadOnlyList<McpActivityEntry> Snapshot()
    {
        lock (Gate)
            return Entries.ToList();
    }

    public static void Clear()
    {
        lock (Gate)
            Entries.Clear();
        RaiseChanged();
    }

    public static void Record(
        string tool,
        object? args,
        string? result,
        TimeSpan duration,
        bool ok,
        string? category = null)
    {
        var cat = category ?? InferCategory(tool);
        var argsText = FormatArgs(args);
        var resultFull = Truncate(result, 32_000);
        var resultSummary = SummarizeResult(resultFull);

        var entry = new McpActivityEntry
        {
            TimeLocal = DateTime.Now,
            Tool = tool,
            ArgsSummary = argsText,
            ResultSummary = resultSummary,
            ResultFull = resultFull,
            DurationMs = (long)duration.TotalMilliseconds,
            Ok = ok,
            Category = cat,
        };

        lock (Gate)
        {
            Entries.Add(entry);
            while (Entries.Count > Capacity)
                Entries.RemoveAt(0);
        }

        if (string.Equals(cat, "library", StringComparison.OrdinalIgnoreCase)
            && tool is "library_search" or "library_get_track" or "track_set_tags"
                or "track_find_master" or "track_link_master" or "track_apply_preset" or "list_tag_presets"
                or "get_library_status" or "library_rescan" or "get_library_config" or "discover_library_roots")
        {
            RaiseLibrary(tool, argsText, resultFull);
        }

        RaiseChanged();
    }

    public static string Run(string tool, object? args, Func<string> body, string? category = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = body();
            Record(tool, args, result, sw.Elapsed, ok: true, category);
            return result;
        }
        catch (Exception ex)
        {
            Record(tool, args, ex.ToString(), sw.Elapsed, ok: false, category);
            throw;
        }
    }

    public static async Task<string> RunAsync(string tool, object? args, Func<Task<string>> body, string? category = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await body().ConfigureAwait(false);
            Record(tool, args, result, sw.Elapsed, ok: true, category);
            return result;
        }
        catch (Exception ex)
        {
            Record(tool, args, ex.ToString(), sw.Elapsed, ok: false, category);
            throw;
        }
    }

    private static string InferCategory(string tool)
    {
        if (tool.StartsWith("library_", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("get_library_", StringComparison.OrdinalIgnoreCase))
            return "library";
        if (tool is "play_pause" or "next_track" or "previous_track" or "volume_up" or "volume_down"
            or "mute_toggle" or "level_volumes" or "shuffle_library" or "fresh_start"
            or "play_favorite_slot" or "set_active_room" or "wake_now" or "wake_cancel")
            return "control";
        if (tool is "refresh_devices" or "list_groups" or "list_zones" or "list_offline"
            or "get_discovery_state" or "get_speaker_volumes" or "get_now_playing" or "list_favorites")
            return "discovery";
        return "general";
    }

    private static string? FormatArgs(object? args)
    {
        if (args is null) return null;
        try
        {
            return Truncate(JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true }), 4_000);
        }
        catch
        {
            return args.ToString();
        }
    }

    private static string SummarizeResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return "(empty)";
        var oneLine = result.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return oneLine.Length <= 160 ? oneLine : oneLine[..157] + "…";
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "\n…(truncated)";
    }

    private static void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        var d = _dispatcher;
        if (d is not null && !d.CheckAccess())
            d.BeginInvoke(() => handler(null, EventArgs.Empty));
        else
            handler(null, EventArgs.Empty);
    }

    private static void RaiseLibrary(string tool, string? args, string? result)
    {
        var handler = LibrarySearchPublished;
        if (handler is null) return;
        var argsObj = new LibrarySearchPublishedEventArgs(tool, args, result);
        var d = _dispatcher;
        if (d is not null && !d.CheckAccess())
            d.BeginInvoke(() => handler(null, argsObj));
        else
            handler(null, argsObj);
    }
}

public sealed class LibrarySearchPublishedEventArgs : EventArgs
{
    public LibrarySearchPublishedEventArgs(string tool, string? args, string? resultJson)
    {
        Tool = tool;
        Args = args;
        ResultJson = resultJson;
    }

    public string Tool { get; }
    public string? Args { get; }
    public string? ResultJson { get; }
}
