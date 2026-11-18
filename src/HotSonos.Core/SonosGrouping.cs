using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>
/// Which rooms to join when a phone (or any client) changes the group leader.
/// The desktop app applies the same SOAP calls with its own pause-hold and Daily allow-list.
/// </summary>
public static class SonosGrouping
{
    public static string RinconUri(string coordinatorUuid) => "x-rincon:" + coordinatorUuid.Trim();

    /// <summary>
    /// Other rooms currently in <paramref name="leader"/>'s group. They should follow
    /// <paramref name="leader"/> after it becomes a standalone coordinator.
    /// Rooms already using <paramref name="leader"/> as coordinator are left out so a
    /// second <c>x-rincon</c> join does not blip playback.
    /// </summary>
    public static IReadOnlyList<SonosZone> FollowersToRejoin(IReadOnlyList<SonosZone> zones, SonosZone leader)
    {
        return zones
            .Where(z => !Same(z.Uuid, leader.Uuid))
            .Where(z => Same(z.CoordinatorUuid, leader.CoordinatorUuid))
            .Where(z => !Same(z.CoordinatorUuid, leader.Uuid))
            .Where(z => !string.IsNullOrWhiteSpace(z.IpAddress))
            .ToList();
    }

    /// <summary>Every other visible room that is not already grouped under <paramref name="leader"/>.</summary>
    public static IReadOnlyList<SonosZone> OthersToJoin(IReadOnlyList<SonosZone> zones, SonosZone leader)
    {
        return zones
            .Where(z => !Same(z.Uuid, leader.Uuid))
            .Where(z => !Same(z.CoordinatorUuid, leader.Uuid))
            .Where(z => !string.IsNullOrWhiteSpace(z.IpAddress))
            .ToList();
    }

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
