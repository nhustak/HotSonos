namespace HotSonos.Remote.Protocol;

/// <summary>
/// Library song row. Tap is Play now (insert after the current track and
/// skip to it; the rest of the queue stays). The ⋮ menu has Play now,
/// Play next, Add to queue, and Toggle tag when talking to the PC.
/// </summary>
public static class LibraryRowActions
{
    public const string PlayNow = "Play now";

    public const string PlayNext = "Play next";

    public const string AddToQueue = "Add to queue";

    public const string ToggleTag = "Toggle tag";

    /// <summary>Visible height of one song row, in device-independent pixels.</summary>
    public const double RowHeight = 48;

    /// <summary>No action buttons are drawn on the row.</summary>
    public static IReadOnlyList<string> Buttons { get; } = [];

    public static IReadOnlyList<string> Sheet(bool tags) =>
        tags
            ? [PlayNow, PlayNext, AddToQueue, ToggleTag]
            : [PlayNow, PlayNext, AddToQueue];
}

/// <summary>
/// Long-press sheet on a Rooms row. Coordinator and reboot are not buttons on the row.
/// Speakers mode has no PC reboot; it offers Leave group so ungrouping still works.
/// </summary>
public static class RoomRowActions
{
    public const string Coordinator = "Make coordinator";

    public const string Reboot = "Reboot";

    public const string Leave = "Leave group";

    public static IReadOnlyList<string> For(bool drivePc) =>
        drivePc ? [Coordinator, Reboot] : [Coordinator, Leave];
}
