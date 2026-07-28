using System.IO;
using System.Text.Json;
using HotSonos.App.Infrastructure;

namespace HotSonos.App.Services;

/// <summary>
/// Durable play lifecycle trail for debugging shuffle/repeats:
/// started, skipped, paused, resumed, previous, stopped.
/// Append-only JSONL under %LocalAppData%\HotSonos\play-events.jsonl + in-memory ring.
/// </summary>
public sealed class PlayEventLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<PlayEvent> _ring = [];
    private const int RingCapacity = 400;
    private const int MaxFileLines = 8000;

    public PlayEventLog(string? path = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "play-events.jsonl");
        LoadTail();
    }

    public string FilePath => _path;

    public void Started(string? uri, string? title, string? artist, string? source = null) =>
        Add("started", uri, title, artist, source);

    public void Skipped(string? uri, string? title, string? artist, string? source = "next") =>
        Add("skipped", uri, title, artist, source);

    public void Paused(string? uri, string? title, string? artist, string? source = null) =>
        Add("paused", uri, title, artist, source);

    public void Resumed(string? uri, string? title, string? artist, string? source = null) =>
        Add("resumed", uri, title, artist, source);

    public void Previous(string? uri, string? title, string? artist, string? source = "previous") =>
        Add("previous", uri, title, artist, source);

    public void Stopped(string? uri, string? title, string? artist, string? source = null) =>
        Add("stopped", uri, title, artist, source);

    /// <summary>Newest last. Optional kind filter (e.g. started, skipped).</summary>
    public IReadOnlyList<PlayEvent> GetRecent(int max = 80, string? kind = null)
    {
        max = Math.Clamp(max, 1, RingCapacity);
        lock (_gate)
        {
            IEnumerable<PlayEvent> q = _ring;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                q = q.Where(e => string.Equals(e.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return q.TakeLast(max).ToList();
        }
    }

    public object Snapshot(int max = 40) => new
    {
        file = _path,
        count = GetRecent(max).Count,
        events = GetRecent(max),
    };

    private void Add(string kind, string? uri, string? title, string? artist, string? source)
    {
        var key = PlayHistoryStore.NormalizeKey(uri);
        var display = FormatDisplay(title, artist, uri, key);
        var evt = new PlayEvent
        {
            Utc = DateTime.UtcNow,
            Kind = kind,
            Title = NullIfEmpty(title),
            Artist = NullIfEmpty(artist),
            Uri = NullIfEmpty(uri?.Trim()),
            Key = key.Length > 0 ? key : null,
            Source = NullIfEmpty(source),
            Display = display,
        };

        lock (_gate)
        {
            _ring.Add(evt);
            while (_ring.Count > RingCapacity)
                _ring.RemoveAt(0);
            AppendFileUnlocked(evt);
        }

        AppLog.Info($"Play {kind}: {display}" +
                    (key.Length > 0 ? $" | key={ShortKey(key)}" : "") +
                    (string.IsNullOrEmpty(source) ? "" : $" | src={source}"));
    }

    private void AppendFileUnlocked(PlayEvent evt)
    {
        try
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(evt, JsonOptions) + Environment.NewLine);
            MaybeTrimFileUnlocked();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Play event log write failed", ex);
        }
    }

    private void MaybeTrimFileUnlocked()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var lines = File.ReadAllLines(_path);
            if (lines.Length <= MaxFileLines) return;
            var keep = lines.TakeLast(MaxFileLines / 2).ToArray();
            File.WriteAllLines(_path, keep);
        }
        catch
        {
            /* best-effort */
        }
    }

    private void LoadTail()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var lines = File.ReadAllLines(_path);
            foreach (var line in lines.TakeLast(RingCapacity))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<PlayEvent>(line, JsonOptions);
                    if (evt is not null)
                        _ring.Add(evt);
                }
                catch
                {
                    /* skip bad line */
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Play event log load failed", ex);
        }
    }

    private static string FormatDisplay(string? title, string? artist, string? uri, string key)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return string.IsNullOrWhiteSpace(artist) ? title.Trim() : $"{title.Trim()} — {artist.Trim()}";
        }

        if (key.Length > 0)
        {
            var slash = key.LastIndexOf('/');
            return slash >= 0 && slash < key.Length - 1 ? key[(slash + 1)..] : key;
        }

        return uri?.Trim() is { Length: > 0 } u ? u : "(unknown track)";
    }

    private static string ShortKey(string key)
    {
        if (key.Length <= 72) return key;
        var slash = key.LastIndexOf('/');
        return slash >= 0 ? "…/" + key[(slash + 1)..] : key[^72..];
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public sealed class PlayEvent
    {
        public DateTime Utc { get; set; }
        public string Kind { get; set; } = "";
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Uri { get; set; }
        public string? Key { get; set; }
        public string? Source { get; set; }
        public string Display { get; set; } = "";
    }
}
