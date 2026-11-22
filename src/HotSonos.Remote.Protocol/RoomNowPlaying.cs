namespace HotSonos.Remote.Protocol;

/// <summary>
/// The line under a room name on the phone Rooms tab.
/// Rooms that share the active group's track show that title.
/// Another group shows Playing or Paused when its transport says so.
/// Stopped, unknown, and a room with nothing loaded stay blank.
/// </summary>
public static class RoomNowPlaying
{
    public static string? Line(
        string? room,
        IReadOnlyList<RemoteTopologyGroupDto>? layout,
        string? activeRoom,
        string? title,
        string? artist)
    {
        if (string.IsNullOrWhiteSpace(room))
            return null;

        var card = Find(layout, room);
        var active = Find(layout, activeRoom);
        var sameGroup = card is not null && active is not null
            && string.Equals(card.GroupId, active.GroupId, StringComparison.OrdinalIgnoreCase);
        if (!sameGroup
            && !string.IsNullOrWhiteSpace(activeRoom)
            && string.Equals(room.Trim(), activeRoom.Trim(), StringComparison.OrdinalIgnoreCase))
            sameGroup = true;

        if (sameGroup)
        {
            var track = Track(title, artist);
            if (track is not null)
                return track;
        }

        var state = card?.PlayState?.Trim();
        return state is "Playing" or "Paused" ? state : null;
    }

    public static string? Track(string? title, string? artist)
    {
        var song = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        var who = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();
        if (song is null && who is null)
            return null;
        if (song is null)
            return who;
        if (who is null)
            return song;
        return song + " — " + who;
    }

    static RemoteTopologyGroupDto? Find(IReadOnlyList<RemoteTopologyGroupDto>? layout, string? room)
    {
        if (layout is null || string.IsNullOrWhiteSpace(room))
            return null;
        foreach (var card in layout)
        {
            if (card.Rooms.Any(r => string.Equals(r.Room, room, StringComparison.OrdinalIgnoreCase)))
                return card;
            if (string.Equals(card.CoordinatorRoom, room, StringComparison.OrdinalIgnoreCase))
                return card;
        }

        return null;
    }
}
