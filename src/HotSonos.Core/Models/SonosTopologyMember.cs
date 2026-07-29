namespace HotSonos.Core.Models;

/// <summary>
/// One ZoneGroupTopology member, including invisible bonded devices (Sub, stereo
/// pair mate, surrounds) that normal control lists omit.
/// </summary>
public sealed record SonosTopologyMember
{
    public required string RoomName { get; init; }
    public required string Uuid { get; init; }
    public required string IpAddress { get; init; }
    public required string CoordinatorUuid { get; init; }
    public required string CoordinatorIpAddress { get; init; }
    public required string GroupId { get; init; }

    /// <summary>True for Sub, stereo mate, surrounds, etc. (not shown as a room).</summary>
    public bool Invisible { get; init; }

    /// <summary>
    /// Bonded channel role from ChannelMapSet when known, e.g. SW, LF, RF, LR.
    /// Null for unbonded / unknown.
    /// </summary>
    public string? ChannelRole { get; init; }

    public bool IsCoordinator =>
        string.Equals(Uuid, CoordinatorUuid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Short label for logs/UI: "Sub (SW · bonded)" or "Theater".</summary>
    public string DisplayLabel
    {
        get
        {
            var role = string.IsNullOrWhiteSpace(ChannelRole) ? null : ChannelRole.Trim().ToUpperInvariant();
            if (Invisible)
            {
                return role is null
                    ? $"{RoomName} (bonded)"
                    : $"{RoomName} ({role} · bonded)";
            }

            return role is null ? RoomName : $"{RoomName} ({role})";
        }
    }
}
