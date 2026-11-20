namespace HotSonos.Core;

/// <summary>
/// One visible player in a ZoneGroupTopology snapshot.
/// <paramref name="GroupId"/> is the coordinator UUID (players that share it are grouped).
/// </summary>
public readonly record struct SpeakerMembership(string Room, string GroupId, bool IsCoordinator);

/// <summary>One live Sonos group, coordinator room first.</summary>
public sealed record LiveGroupView(
    string Id,
    string CoordinatorRoom,
    IReadOnlyList<string> Rooms,
    string Label);

/// <summary>
/// House grouping as Sonos reports it. This is not the Daily group preset.
/// </summary>
public sealed record LiveGroupingView(
    IReadOnlyList<LiveGroupView> Groups,
    string Summary);

/// <summary>
/// Turns ZoneGroupTopology membership into the line the tray and the phone show.
/// Does not read settings and does not change speakers.
/// </summary>
public static class LiveGrouping
{
    public const string DriftMessage = "Not grouped as Daily group, tap Regroup";
    public const string AllSpeakers = "All speakers";
    public const string NoSpeakers = "No speakers";

    public static LiveGroupingView Map(IEnumerable<SpeakerMembership>? speakers)
    {
        var rows = new List<SpeakerMembership>();
        if (speakers is not null)
        {
            foreach (var speaker in speakers)
            {
                var room = (speaker.Room ?? "").Trim();
                if (room.Length == 0)
                    continue;
                rows.Add(speaker with { Room = room });
            }
        }

        var groups = rows
            .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(BuildGroup)
            .OrderByDescending(g => g.Rooms.Count)
            .ThenBy(g => g.CoordinatorRoom, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LiveGroupingView(groups, Summarize(groups));
    }

    /// <summary>
    /// True when the Daily preset's rooms are actually together.
    /// All speakers (or an empty list) matches only when every visible room shares one group.
    /// A room list matches when each of those rooms is visible and they share one group.
    /// Rooms outside the list may sit in other groups. An empty house is not drift.
    /// </summary>
    public static bool MatchesDaily(LiveGroupingView live, bool allSpeakers, IEnumerable<string>? dailyRooms)
    {
        if (live.Groups.Count == 0)
            return true;

        var wanted = allSpeakers ? [] : Normalize(dailyRooms);
        if (allSpeakers || wanted.Count == 0)
            return live.Groups.Count == 1;

        var groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in live.Groups)
        {
            foreach (var room in group.Rooms)
                groupOf[room] = group.Id;
        }

        string? together = null;
        foreach (var room in wanted)
        {
            if (!groupOf.TryGetValue(room, out var id))
                return false;
            if (together is null)
                together = id;
            else if (!string.Equals(together, id, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    static LiveGroupView BuildGroup(IGrouping<string, SpeakerMembership> group)
    {
        var coordinator = group.FirstOrDefault(s => s.IsCoordinator);
        var lead = coordinator.Room;
        if (string.IsNullOrEmpty(lead))
        {
            lead = group
                .Select(s => s.Room)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        var rooms = new List<string> { lead };
        foreach (var room in group
            .Select(s => s.Room)
            .Where(r => !string.Equals(r, lead, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            rooms.Add(room);
        }

        var label = rooms.Count == 1 ? rooms[0] : string.Join(" + ", rooms);
        return new LiveGroupView(group.Key, rooms[0], rooms, label);
    }

    static string Summarize(IReadOnlyList<LiveGroupView> groups)
    {
        if (groups.Count == 0)
            return NoSpeakers;
        if (groups.Count == 1)
            return groups[0].Rooms.Count > 1 ? AllSpeakers : groups[0].CoordinatorRoom;

        var multi = groups.Where(g => g.Rooms.Count > 1).ToList();
        var solos = groups.Count - multi.Count;
        var parts = new List<string>(multi.Count + 1);
        foreach (var group in multi)
            parts.Add(group.Label);
        if (solos > 0)
            parts.Add(solos == 1 ? "1 room ungrouped" : $"{solos} rooms ungrouped");
        return string.Join("; ", parts);
    }

    static string GroupKey(SpeakerMembership speaker)
    {
        var id = (speaker.GroupId ?? "").Trim();
        return id.Length == 0 ? "solo:" + speaker.Room : id;
    }

    static List<string> Normalize(IEnumerable<string>? rooms)
    {
        var list = new List<string>();
        if (rooms is null)
            return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rooms)
        {
            var room = (raw ?? "").Trim();
            if (room.Length == 0 || !seen.Add(room))
                continue;
            list.Add(room);
        }

        return list;
    }
}
