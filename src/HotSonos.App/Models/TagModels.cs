namespace HotSonos.App.Models;

/// <summary>
/// One user tag: opaque <see cref="Key"/> stored in files; <see cref="Label"/> is display-only and renamable.
/// </summary>
public sealed class TagDefinition
{
    /// <summary>Stable auto-generated id written into <c>HOTSONOS_TAGS</c>. Never shown as the primary UI name.</summary>
    public string Key { get; set; } = "";

    /// <summary>User-facing name (rename freely; files keep <see cref="Key"/>).</summary>
    public string Label { get; set; } = "";
}
