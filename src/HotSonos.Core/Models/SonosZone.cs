namespace HotSonos.Core.Models;

/// <summary>
/// A single Sonos player as reported by ZoneGroupTopology, plus the group it
/// belongs to. Transport commands must be sent to the group <b>coordinator</b>,
/// so each zone carries the coordinator's UUID/IP for its group.
/// </summary>
public sealed record SonosZone
{
    /// <summary>Room name shown in the Sonos app, e.g. "Living Room".</summary>
    public required string RoomName { get; init; }

    /// <summary>Player UUID, e.g. "RINCON_XXXXXXXXXXXX01400".</summary>
    public required string Uuid { get; init; }

    /// <summary>IP address of this player.</summary>
    public required string IpAddress { get; init; }

    /// <summary>UUID of the coordinator for this player's group.</summary>
    public required string CoordinatorUuid { get; init; }

    /// <summary>IP of the coordinator for this player's group (where commands go).</summary>
    public required string CoordinatorIpAddress { get; init; }

    /// <summary>Group identifier from the topology.</summary>
    public required string GroupId { get; init; }

    /// <summary>True when this player coordinates its own group.</summary>
    public bool IsCoordinator => string.Equals(Uuid, CoordinatorUuid, StringComparison.OrdinalIgnoreCase);
}
