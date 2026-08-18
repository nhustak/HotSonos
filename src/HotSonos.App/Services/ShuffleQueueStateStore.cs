using System.IO;
using System.Text.Json;
using HotSonos.App.Infrastructure;

namespace HotSonos.App.Services;

/// <summary>
/// Persists the last library shuffle rebuild so we can detect a leftover Sonos
/// queue after app/speaker restart (NORMAL mode keeps yesterday's order).
/// File: %LocalAppData%\HotSonos\shuffle-queue-state.json
/// </summary>
public sealed class ShuffleQueueStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private StateDoc _doc = new();

    public ShuffleQueueStateStore(string? path = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "shuffle-queue-state.json");
        Load();
    }

    public string FilePath => _path;

    public DateTime? LastRebuildUtc
    {
        get { lock (_gate) return _doc.LastRebuildUtc; }
    }

    public DateTime? LastQueueChangeUtc
    {
        get { lock (_gate) return _doc.LastQueueChangeUtc; }
    }

    public int LastRebuildQueueSize
    {
        get { lock (_gate) return _doc.LastRebuildQueueSize; }
    }

    public int LastKnownTrackIndex
    {
        get { lock (_gate) return _doc.LastKnownTrackIndex; }
    }

    public int LastKnownQueueTotal
    {
        get { lock (_gate) return _doc.LastKnownQueueTotal; }
    }

    public string Mode
    {
        get { lock (_gate) return _doc.Mode ?? "none"; }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                _doc.LastRebuildUtc,
                _doc.LastQueueChangeUtc,
                _doc.LastRebuildQueueSize,
                _doc.LastKnownTrackIndex,
                _doc.LastKnownQueueTotal,
                _doc.Mode,
                sampleKeys = _doc.SampleKeys,
                ageHours = _doc.LastRebuildUtc is DateTime t
                    ? Math.Round((DateTime.UtcNow - t).TotalHours, 2)
                    : (double?)null,
            };
        }
    }

    /// <summary>Debug: pretend the last shuffle rebuild was <paramref name="age"/> ago.</summary>
    public void BackdateRebuild(TimeSpan age)
    {
        lock (_gate)
        {
            _doc.LastRebuildUtc = DateTime.UtcNow - age;
            SaveUnlocked();
        }
    }

    /// <summary>True when we have never rebuilt, or rebuild is older than <paramref name="maxAge"/>.</summary>
    public bool IsRebuildStale(TimeSpan maxAge)
    {
        lock (_gate)
        {
            if (_doc.LastRebuildUtc is not DateTime t)
                return true;
            return DateTime.UtcNow - t > maxAge;
        }
    }

    /// <summary>Full queue replace (history-aware shuffle rebuild).</summary>
    public void RecordRebuild(int enqueued, string mode, IReadOnlyList<string>? sampleUris = null)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _doc.LastRebuildUtc = now;
            _doc.LastQueueChangeUtc = now;
            _doc.LastRebuildQueueSize = Math.Max(0, enqueued);
            _doc.LastKnownTrackIndex = 1;
            _doc.LastKnownQueueTotal = Math.Max(0, enqueued);
            _doc.Mode = string.IsNullOrWhiteSpace(mode) ? "shuffle" : mode;
            _doc.SampleKeys = BuildSampleKeys(sampleUris);
            SaveUnlocked();
        }
    }

    /// <summary>Append top-up ΓÇö keeps rebuild timestamp, bumps activity.</summary>
    public void RecordTopUp(int appended, int? newQueueTotal = null)
    {
        if (appended <= 0 && newQueueTotal is null)
            return;
        lock (_gate)
        {
            _doc.LastQueueChangeUtc = DateTime.UtcNow;
            if (newQueueTotal is int t && t > 0)
                _doc.LastKnownQueueTotal = t;
            else if (appended > 0)
                _doc.LastKnownQueueTotal = Math.Max(0, _doc.LastKnownQueueTotal) + appended;
            SaveUnlocked();
        }
    }

    public void ObservePosition(int? currentTrack, int? numberOfTracks)
    {
        lock (_gate)
        {
            var dirty = false;
            if (currentTrack is int c && c > 0 && c != _doc.LastKnownTrackIndex)
            {
                _doc.LastKnownTrackIndex = c;
                dirty = true;
            }
            if (numberOfTracks is int n && n > 0 && n != _doc.LastKnownQueueTotal)
            {
                _doc.LastKnownQueueTotal = n;
                dirty = true;
            }
            if (dirty)
                SaveUnlocked();
        }
    }

    private static List<string> BuildSampleKeys(IReadOnlyList<string>? uris)
    {
        if (uris is null || uris.Count == 0)
            return [];
        var list = new List<string>(Math.Min(8, uris.Count));
        foreach (var u in uris.Take(4))
        {
            var k = PlayHistoryStore.NormalizeKey(u);
            if (k.Length > 0)
                list.Add(k);
        }
        if (uris.Count > 4)
        {
            foreach (var u in uris.Skip(Math.Max(0, uris.Count - 2)))
            {
                var k = PlayHistoryStore.NormalizeKey(u);
                if (k.Length > 0 && !list.Contains(k, StringComparer.OrdinalIgnoreCase))
                    list.Add(k);
            }
        }
        return list;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<StateDoc>(json, JsonOptions);
            if (doc is not null)
                _doc = doc;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Shuffle queue state load failed; starting empty", ex);
            _doc = new StateDoc();
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
            AppLog.Warn("Shuffle queue state save failed", ex);
        }
    }

    private sealed class StateDoc
    {
        public DateTime? LastRebuildUtc { get; set; }
        public DateTime? LastQueueChangeUtc { get; set; }
        public int LastRebuildQueueSize { get; set; }
        public int LastKnownTrackIndex { get; set; }
        public int LastKnownQueueTotal { get; set; }
        public string? Mode { get; set; }
        public List<string> SampleKeys { get; set; } = [];
    }
}
