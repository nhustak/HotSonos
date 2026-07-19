using System.IO;
using System.Text.Json;
using HotSonos.App.Infrastructure;

namespace HotSonos.App.Services;

/// <summary>
/// Tracks URIs that were actually played (GENA) so the next queue rebuild
/// can hard-exclude them. History only matters at rebuild/top-up time.
/// </summary>
public sealed class PlayHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Func<int> _retentionDays;
    private readonly object _gate = new();
    private HistoryDoc _doc = new();

    public int MaxEntries { get; init; } = 5000;

    public PlayHistoryStore(Func<int>? retentionDays = null, string? path = null)
    {
        _retentionDays = retentionDays ?? (() => 14);
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos");
        Directory.CreateDirectory(dir);
        _path = path ?? System.IO.Path.Combine(dir, "play-history.json");
        Load();
    }

    public string FilePath => _path;

    public int RetentionDays => Math.Clamp(_retentionDays(), 1, 90);

    /// <summary>Normalized keys for URIs played within the retention window.</summary>
    public IReadOnlyCollection<string> GetPlayedKeys()
    {
        lock (_gate)
        {
            PruneUnlocked();
            return _doc.Entries
                .Where(e => string.Equals(e.Kind, "played", StringComparison.OrdinalIgnoreCase))
                .Select(e => NormalizeKey(e.Uri))
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public int PlayedDistinctCount
    {
        get
        {
            lock (_gate)
            {
                PruneUnlocked();
                return _doc.Entries
                    .Where(e => string.Equals(e.Kind, "played", StringComparison.OrdinalIgnoreCase))
                    .Select(e => NormalizeKey(e.Uri))
                    .Where(k => k.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            }
        }
    }

    /// <summary>Clears all stored play history. Returns how many entries were removed.</summary>
    public int Clear()
    {
        lock (_gate)
        {
            var n = _doc.Entries.Count;
            _doc.Entries.Clear();
            SaveUnlocked();
            AppLog.Info($"Play history cleared ({n} entries removed)");
            return n;
        }
    }

    public void RecordPlayed(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        var key = NormalizeKey(uri);
        if (key.Length == 0) return;

        if (!uri.Contains("file-cifs", StringComparison.OrdinalIgnoreCase)
            && !uri.StartsWith(@"\\", StringComparison.Ordinal))
            return;

        lock (_gate)
        {
            var last = _doc.Entries.LastOrDefault(e =>
                string.Equals(NormalizeKey(e.Uri), key, StringComparison.OrdinalIgnoreCase));
            if (last is not null && (DateTime.UtcNow - last.Utc).TotalSeconds < 90)
                return;

            _doc.Entries.Add(new HistoryEntry
            {
                Uri = uri.Trim(),
                Key = key,
                Utc = DateTime.UtcNow,
                Kind = "played",
            });
            PruneUnlocked();
            SaveUnlocked();
        }
    }

    public static string NormalizeKey(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return "";
        var s = uri.Trim();
        try { s = Uri.UnescapeDataString(s); } catch { /* keep */ }
        s = s.Replace('\\', '/');
        const string prefix = "x-file-cifs://";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            s = s[prefix.Length..];
        var q = s.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0) s = s[..q];
        return s.Trim().ToLowerInvariant();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<HistoryDoc>(json, JsonOptions);
            if (doc?.Entries is not null)
                _doc = doc;
            PruneUnlocked();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Play history load failed; starting empty", ex);
            _doc = new HistoryDoc();
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_doc, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Play history save failed", ex);
        }
    }

    private void PruneUnlocked()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        _doc.Entries = _doc.Entries
            .Where(e => e.Utc >= cutoff
                        && !string.IsNullOrWhiteSpace(e.Uri)
                        && string.Equals(e.Kind, "played", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => NormalizeKey(e.Uri), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.Utc).First())
            .OrderByDescending(e => e.Utc)
            .Take(MaxEntries)
            .ToList();
    }

    private sealed class HistoryDoc
    {
        public List<HistoryEntry> Entries { get; set; } = [];
    }

    private sealed class HistoryEntry
    {
        public string Uri { get; set; } = "";
        public string? Key { get; set; }
        public DateTime Utc { get; set; }
        public string Kind { get; set; } = "played";
    }
}
