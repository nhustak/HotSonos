using System.IO;
using System.Text.Json;
using HotSonos.App.Infrastructure;
using HotSonos.Core.Models;

namespace HotSonos.App.Services;

/// <summary>
/// Durable topology / grouping trail for monitoring rooms, Port, Sub, and
/// other bonded devices going in/out of groups or vanishing.
/// Append-only JSONL under %LocalAppData%\HotSonos\topology-events.jsonl + ring.
/// </summary>
public sealed class TopologyEventLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<TopologyEvent> _ring = [];
    private const int RingCapacity = 600;
    private const int MaxFileLines = 12000;

    private SonosTopologySnapshot? _previous;
    private bool _hasBaseline;

    public TopologyEventLog(string? path = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "topology-events.jsonl");
        LoadTail();
    }

    public string FilePath => _path;

    public event Action? Changed;

    /// <summary>
    /// Diff <paramref name="snap"/> against the last snapshot and append events.
    /// First call logs a baseline only (no flood of "appeared" for every room).
    /// </summary>
    public void Observe(SonosTopologySnapshot snap, string source = "gena")
    {
        ArgumentNullException.ThrowIfNull(snap);

        var added = 0;
        lock (_gate)
        {
            if (!_hasBaseline || _previous is null)
            {
                _previous = snap;
                _hasBaseline = true;
                AddUnlocked(new TopologyEvent
                {
                    Utc = DateTime.UtcNow,
                    Kind = "baseline",
                    Display = SummarizeSnapshot(snap),
                    Source = source,
                    GroupCount = snap.GroupCount,
                    VisibleCount = snap.VisibleCount,
                    InvisibleCount = snap.InvisibleCount,
                    Detail = SnapshotDetail(snap),
                }, raiseChanged: false);
                added = 1;
            }
            else
            {
                var prev = _previous;
                _previous = snap;
                added = EmitDiffsUnlocked(prev, snap, source);
            }
        }

        // One UI notify per GENA snapshot — not one per member event (was thrashing WPF).
        if (added > 0)
        {
            try { Changed?.Invoke(); }
            catch { /* UI must not break logging */ }
        }
    }

    /// <summary>Newest last. Optional kind filter.</summary>
    public IReadOnlyList<TopologyEvent> GetRecent(int max = 100, string? kind = null)
    {
        max = Math.Clamp(max, 1, RingCapacity);
        lock (_gate)
        {
            IEnumerable<TopologyEvent> q = _ring;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                q = q.Where(e => string.Equals(e.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return q.TakeLast(max).ToList();
        }
    }

    public object Snapshot(int max = 50) => new
    {
        file = _path,
        count = GetRecent(max).Count,
        events = GetRecent(max),
        current = _previous is null ? null : new
        {
            groupCount = _previous.GroupCount,
            visible = _previous.VisibleCount,
            invisible = _previous.InvisibleCount,
            subs = _previous.Subs.Select(s => s.DisplayLabel).ToList(),
            members = _previous.Members.Select(m => new
            {
                m.RoomName,
                m.Uuid,
                m.IpAddress,
                m.Invisible,
                m.ChannelRole,
                m.GroupId,
                coordinator = m.IsCoordinator,
                label = m.DisplayLabel,
            }).ToList(),
            vanished = _previous.VanishedRooms,
        },
    };

    private int EmitDiffsUnlocked(SonosTopologySnapshot prev, SonosTopologySnapshot curr, string source)
    {
        var added = 0;
        var prevByUuid = prev.Members.ToDictionary(m => m.Uuid, StringComparer.OrdinalIgnoreCase);
        var currByUuid = curr.Members.ToDictionary(m => m.Uuid, StringComparer.OrdinalIgnoreCase);

        if (prev.GroupCount != curr.GroupCount)
        {
            AddUnlocked(new TopologyEvent
            {
                Utc = DateTime.UtcNow,
                Kind = "groups_changed",
                Display = $"Groups {prev.GroupCount} → {curr.GroupCount}  ({SummarizeGroups(curr)})",
                Source = source,
                GroupCount = curr.GroupCount,
                VisibleCount = curr.VisibleCount,
                InvisibleCount = curr.InvisibleCount,
                Detail = SnapshotDetail(curr),
            }, raiseChanged: false);
            added++;
        }

        // Vanished list (Sonos "dropped off network").
        var prevVan = new HashSet<string>(prev.VanishedRooms, StringComparer.OrdinalIgnoreCase);
        var currVan = new HashSet<string>(curr.VanishedRooms, StringComparer.OrdinalIgnoreCase);
        foreach (var room in currVan.Where(r => !prevVan.Contains(r)))
        {
            AddUnlocked(new TopologyEvent
            {
                Utc = DateTime.UtcNow,
                Kind = "vanished",
                RoomName = room,
                Display = $"⚠️ Vanished (network): {room}",
                Source = source,
                GroupCount = curr.GroupCount,
            }, raiseChanged: false);
            added++;
        }

        foreach (var room in prevVan.Where(r => !currVan.Contains(r)))
        {
            AddUnlocked(new TopologyEvent
            {
                Utc = DateTime.UtcNow,
                Kind = "returned",
                RoomName = room,
                Display = $"✓ Returned (network): {room}",
                Source = source,
                GroupCount = curr.GroupCount,
            }, raiseChanged: false);
            added++;
        }

        foreach (var m in curr.Members.Where(m => !prevByUuid.ContainsKey(m.Uuid)))
        {
            AddUnlocked(MakeMemberEvent(
                kind: m.Invisible ? "bonded_appeared" : "appeared",
                member: m,
                display: m.Invisible
                    ? $"+ Bonded device online: {m.DisplayLabel} → {GroupLabel(curr, m)}"
                    : $"+ Online: {m.DisplayLabel} → {GroupLabel(curr, m)}",
                source: source,
                groupCount: curr.GroupCount), raiseChanged: false);
            added++;
        }

        foreach (var m in prev.Members.Where(m => !currByUuid.ContainsKey(m.Uuid)))
        {
            AddUnlocked(MakeMemberEvent(
                kind: m.Invisible ? "bonded_disappeared" : "disappeared",
                member: m,
                display: m.Invisible
                    ? $"− Bonded device gone: {m.DisplayLabel} (was {GroupLabel(prev, m)})"
                    : $"− Offline/left topology: {m.DisplayLabel} (was {GroupLabel(prev, m)})",
                source: source,
                groupCount: curr.GroupCount), raiseChanged: false);
            added++;
        }

        foreach (var m in curr.Members)
        {
            if (!prevByUuid.TryGetValue(m.Uuid, out var old))
                continue;

            var groupChanged = !string.Equals(old.GroupId, m.GroupId, StringComparison.OrdinalIgnoreCase)
                               || !string.Equals(old.CoordinatorUuid, m.CoordinatorUuid, StringComparison.OrdinalIgnoreCase);
            if (!groupChanged)
                continue;

            var wasAlone = prev.Members.Count(x =>
                string.Equals(x.GroupId, old.GroupId, StringComparison.OrdinalIgnoreCase)) <= 1;
            var nowAlone = curr.Members.Count(x =>
                string.Equals(x.GroupId, m.GroupId, StringComparison.OrdinalIgnoreCase)) <= 1;

            string kind;
            string display;
            if (wasAlone && !nowAlone)
            {
                kind = "joined_group";
                display = $"→ Joined group: {m.DisplayLabel} → {GroupLabel(curr, m)}";
            }
            else if (!wasAlone && nowAlone)
            {
                kind = "left_group";
                display = $"← Left group: {m.DisplayLabel} (was {GroupLabel(prev, old)}, now alone)";
            }
            else
            {
                kind = "moved_group";
                display = $"↔ Moved group: {m.DisplayLabel}: {GroupLabel(prev, old)} → {GroupLabel(curr, m)}";
            }

            AddUnlocked(MakeMemberEvent(kind, m, display, source, curr.GroupCount), raiseChanged: false);
            added++;
        }

        return added;
    }

    private static TopologyEvent MakeMemberEvent(
        string kind,
        SonosTopologyMember member,
        string display,
        string source,
        int groupCount) =>
        new()
        {
            Utc = DateTime.UtcNow,
            Kind = kind,
            RoomName = member.RoomName,
            Uuid = member.Uuid,
            IpAddress = member.IpAddress,
            Invisible = member.Invisible,
            ChannelRole = member.ChannelRole,
            GroupId = member.GroupId,
            CoordinatorUuid = member.CoordinatorUuid,
            Display = display,
            Source = source,
            GroupCount = groupCount,
        };

    private void AddUnlocked(TopologyEvent evt, bool raiseChanged = true)
    {
        _ring.Add(evt);
        while (_ring.Count > RingCapacity)
            _ring.RemoveAt(0);
        AppendFileUnlocked(evt);

        // Only log significant kinds to the main app log (file JSONL still has everything).
        if (evt.Kind is "baseline" or "groups_changed" or "vanished" or "returned"
            or "left_group" or "bonded_disappeared")
        {
            AppLog.Info($"Topology {evt.Kind}: {evt.Display}" +
                        (string.IsNullOrEmpty(evt.Source) ? "" : $" | src={evt.Source}"));
        }

        if (raiseChanged)
        {
            try { Changed?.Invoke(); }
            catch { /* UI subscribers must not break logging */ }
        }
    }

    private void AppendFileUnlocked(TopologyEvent evt)
    {
        try
        {
            // Append only — trim is rare and expensive (was re-reading whole file on flaps).
            File.AppendAllText(_path, JsonSerializer.Serialize(evt, JsonOptions) + Environment.NewLine);
            if (_ring.Count % 200 == 0)
                MaybeTrimFileUnlocked();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Topology event log write failed", ex);
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
            foreach (var line in File.ReadAllLines(_path).TakeLast(RingCapacity))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<TopologyEvent>(line, JsonOptions);
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
            AppLog.Warn("Topology event log load failed", ex);
        }
    }

    private static string SummarizeSnapshot(SonosTopologySnapshot snap)
    {
        var subs = snap.Subs.Select(s => s.DisplayLabel).ToList();
        var subPart = subs.Count == 0 ? "no Sub in topology" : "Sub: " + string.Join(", ", subs);
        return $"Baseline: {snap.VisibleCount} room(s), {snap.InvisibleCount} bonded, {snap.GroupCount} group(s); {subPart}; {SummarizeGroups(snap)}";
    }

    private static string SummarizeGroups(SonosTopologySnapshot snap)
    {
        var parts = snap.Members
            .Where(m => !m.Invisible)
            .GroupBy(m => m.GroupId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var coord = g.FirstOrDefault(x => x.IsCoordinator)?.RoomName
                            ?? g.First().RoomName;
                var n = g.Count();
                return n <= 1 ? coord : $"{coord}+{n - 1}";
            })
            .OrderByDescending(s => s.Length)
            .ToList();
        return parts.Count == 0 ? "(empty)" : string.Join(" | ", parts);
    }

    private static string GroupLabel(SonosTopologySnapshot snap, SonosTopologyMember m)
    {
        var visible = snap.Members
            .Where(x => string.Equals(x.GroupId, m.GroupId, StringComparison.OrdinalIgnoreCase) && !x.Invisible)
            .ToList();
        var coord = visible.FirstOrDefault(x => x.IsCoordinator)?.RoomName
                    ?? snap.Members.FirstOrDefault(x =>
                        string.Equals(x.Uuid, m.CoordinatorUuid, StringComparison.OrdinalIgnoreCase))?.RoomName
                    ?? m.RoomName;
        if (visible.Count <= 1)
            return coord;
        return $"{coord}+{visible.Count - 1}";
    }

    private static string SnapshotDetail(SonosTopologySnapshot snap)
    {
        var lines = snap.Members
            .OrderBy(m => m.Invisible)
            .ThenBy(m => m.RoomName, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"{m.DisplayLabel}@{m.IpAddress} → {GroupLabel(snap, m)}");
        var van = snap.VanishedRooms.Count == 0
            ? ""
            : " | vanished=[" + string.Join(", ", snap.VanishedRooms) + "]";
        return string.Join("; ", lines) + van;
    }

    public sealed class TopologyEvent
    {
        public DateTime Utc { get; set; }
        public string Kind { get; set; } = "";
        public string Display { get; set; } = "";
        public string? RoomName { get; set; }
        public string? Uuid { get; set; }
        public string? IpAddress { get; set; }
        public bool Invisible { get; set; }
        public string? ChannelRole { get; set; }
        public string? GroupId { get; set; }
        public string? CoordinatorUuid { get; set; }
        public string? Source { get; set; }
        public int? GroupCount { get; set; }
        public int? VisibleCount { get; set; }
        public int? InvisibleCount { get; set; }
        public string? Detail { get; set; }

        /// <summary>UI line with local time.</summary>
        public string HeaderLine =>
            $"{Utc.ToLocalTime():HH:mm:ss}  {Display}";
    }
}
