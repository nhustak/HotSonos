using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>One ZoneGroupTopology member, including a bonded unit that is not its own room.</summary>
public readonly record struct TopologyMemberInput(
    string Room,
    string GroupId,
    bool IsCoordinator,
    bool Invisible,
    string? Role,
    string? Product);

/// <summary>A visible room with bonded units (Sub, stereo mate) folded under it.</summary>
public sealed record TopologyRoom(
    string Room,
    bool IsCoordinator,
    IReadOnlyList<string> Folded);

/// <summary>
/// One live Sonos group. A single visible room is <see cref="Ungrouped"/> and stands on its own.
/// </summary>
public sealed record TopologyGroupCard(
    string GroupId,
    string CoordinatorRoom,
    bool Ungrouped,
    IReadOnlyList<TopologyRoom> Rooms);

/// <summary>
/// Turns a ZoneGroupState member list into the cards the phone shows.
/// This is the live topology, not the Daily group preset.
/// </summary>
public static class TopologyLayout
{
    public static string PlayLabel(SonosTransportState state) => state switch
    {
        SonosTransportState.Playing or SonosTransportState.Transitioning => "Playing",
        SonosTransportState.PausedPlayback => "Paused",
        SonosTransportState.Stopped => "Stopped",
        _ => "Unknown",
    };

    public static IReadOnlyList<TopologyGroupCard> Map(IEnumerable<TopologyMemberInput>? members)
    {
        var rows = new List<TopologyMemberInput>();
        if (members is not null)
        {
            foreach (var member in members)
            {
                var room = (member.Room ?? "").Trim();
                if (room.Length == 0)
                    continue;
                var id = string.IsNullOrWhiteSpace(member.GroupId) ? "solo:" + room : member.GroupId.Trim();
                rows.Add(member with { Room = room, GroupId = id });
            }
        }

        var cards = new List<TopologyGroupCard>();
        foreach (var group in rows.GroupBy(m => m.GroupId, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            var visible = list.Where(m => !m.Invisible).ToList();
            var coordinator = visible.FirstOrDefault(m => m.IsCoordinator);
            if (string.IsNullOrEmpty(coordinator.Room))
            {
                coordinator = visible
                    .OrderBy(m => m.Room, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }

            if (string.IsNullOrEmpty(coordinator.Room))
                coordinator = list[0];

            var visibleRooms = visible
                .Select(m => m.Room)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (visibleRooms.Count == 0)
                visibleRooms.Add(coordinator.Room);

            var foldedByRoom = visibleRooms.ToDictionary(
                r => r,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var hidden in list.Where(m => m.Invisible))
            {
                var host = HostRoom(hidden.Room, visibleRooms, coordinator.Room);
                var label = FoldLabel(hidden);
                if (!foldedByRoom[host].Contains(label, StringComparer.OrdinalIgnoreCase))
                    foldedByRoom[host].Add(label);
            }

            var rooms = visibleRooms
                .OrderByDescending(r => string.Equals(r, coordinator.Room, StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => r, StringComparer.OrdinalIgnoreCase)
                .Select(r =>
                {
                    var folded = foldedByRoom[r]
                        .OrderBy(f => string.Equals(f, "Sub", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return new TopologyRoom(
                        r,
                        string.Equals(r, coordinator.Room, StringComparison.OrdinalIgnoreCase),
                        folded);
                })
                .ToList();

            cards.Add(new TopologyGroupCard(
                group.Key,
                coordinator.Room,
                visible.Select(m => m.Room).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1,
                rooms));
        }

        return cards
            .OrderBy(c => c.Ungrouped)
            .ThenByDescending(c => c.Rooms.Count)
            .ThenBy(c => c.CoordinatorRoom, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string HostRoom(string hiddenRoom, IReadOnlyList<string> visibleRooms, string coordinatorRoom)
    {
        foreach (var room in visibleRooms)
        {
            if (string.Equals(room, hiddenRoom, StringComparison.OrdinalIgnoreCase))
                return room;
        }

        string? best = null;
        foreach (var room in visibleRooms)
        {
            if (hiddenRoom.StartsWith(room + " ", StringComparison.OrdinalIgnoreCase)
                || hiddenRoom.StartsWith(room + "(", StringComparison.OrdinalIgnoreCase))
            {
                if (best is null || room.Length > best.Length)
                    best = room;
            }
        }

        return best ?? coordinatorRoom;
    }

    static string FoldLabel(TopologyMemberInput member)
    {
        if (string.Equals(member.Role, "SW", StringComparison.OrdinalIgnoreCase)
            || Contains(member.Room, "Sub")
            || Contains(member.Product, "Sub"))
            return "Sub";
        if (!string.IsNullOrWhiteSpace(member.Role))
            return member.Role.Trim();
        if (!string.IsNullOrWhiteSpace(member.Product))
            return member.Product.Trim();
        return "Bonded";
    }

    static bool Contains(string? text, string part) =>
        !string.IsNullOrWhiteSpace(text) && text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
