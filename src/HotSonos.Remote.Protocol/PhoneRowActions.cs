namespace HotSonos.Remote.Protocol;

/// <summary>
/// Library song row. Tap plays the song. Wipe queue and tags are on a
/// long-press sheet, not buttons on the row. Queue is not an action.
/// </summary>
public static class LibraryRowActions
{
    public const string Play = "Play";

    public const string Wipe = "Wipe queue";

    public const string ToggleTag = "Toggle a tag…";

    /// <summary>Visible height of one song row, in device-independent pixels.</summary>
    public const double RowHeight = 48;

    /// <summary>No action buttons are drawn on the row.</summary>
    public static IReadOnlyList<string> Buttons { get; } = [];

    public static IReadOnlyList<string> Sheet(bool tags) =>
        tags ? [Wipe, ToggleTag] : [Wipe];
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
