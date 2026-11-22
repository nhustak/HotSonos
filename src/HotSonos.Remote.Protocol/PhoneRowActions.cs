namespace HotSonos.Remote.Protocol;

/// <summary>
/// Buttons drawn on each Library song row.
/// Queue is not one of them. The white Queue control was removed from the row.
/// </summary>
public static class LibraryRowActions
{
    public const string Play = "Play";

    public const string Wipe = "Wipe queue";

    public static IReadOnlyList<string> Buttons { get; } = [Play, Wipe];
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
