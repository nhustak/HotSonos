namespace HotSonos.Core.Models;

/// <summary>Full ZoneGroupState parse: visible rooms + bonded/invisible + vanished.</summary>
public sealed class SonosTopologySnapshot
{
    public IReadOnlyList<SonosTopologyMember> Members { get; init; } = [];
    public IReadOnlyList<string> VanishedRooms { get; init; } = [];

    public int VisibleCount => Members.Count(m => !m.Invisible);
    public int InvisibleCount => Members.Count(m => m.Invisible);
    public int GroupCount => Members.Select(m => m.GroupId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public IReadOnlyList<SonosTopologyMember> Subs =>
        Members.Where(m =>
                m.Invisible
                && (string.Equals(m.ChannelRole, "SW", StringComparison.OrdinalIgnoreCase)
                    || m.RoomName.Contains("Sub", StringComparison.OrdinalIgnoreCase)))
            .ToList();
}
