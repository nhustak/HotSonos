namespace HotSonos.Core;

/// <summary>
/// A saved speaker bond layout: exact ChannelMapSet values for the rooms it affects.
/// Unlisted rooms are never touched when the layout is applied.
/// </summary>
public sealed class SpeakerLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public List<LayoutRoom> Rooms { get; set; } = [];
}

/// <summary>One room this layout may change, with the exact bond map to restore.</summary>
public sealed class LayoutRoom
{
    public string RoomName { get; set; } = "";
    public string PrimaryUuid { get; set; } = "";
    public string? RightUuid { get; set; }
    public string? SubUuid { get; set; }

    /// <summary>Exact ChannelMapSet, or empty if this room should be an unbonded standalone.</summary>
    public string ChannelMapSet { get; set; } = "";
}

/// <summary>Result of applying a <see cref="SpeakerLayout"/>.</summary>
public sealed record BondApplyResult(bool Ok, string Message, bool NoOp = false, string? FailedStep = null);
