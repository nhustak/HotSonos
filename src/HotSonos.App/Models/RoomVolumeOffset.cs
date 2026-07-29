namespace HotSonos.App.Models;

/// <summary>
/// Per-room additive offset for absolute volume targets (Level all, wake ramp).
/// Example: Media Room +60 so a house level of 20% becomes 80% on that Sonos port
/// (useful when a Port feeds a non–Sonos-ready amp).
/// Relative volume steps and manual per-speaker sliders stay raw Sonos %.
/// </summary>
public sealed class RoomVolumeOffset
{
    /// <summary>Sonos room name (matches topology zone name).</summary>
    public string RoomName { get; set; } = "";

    /// <summary>Added to logical level, then clamped 0–100. Range −100…+100.</summary>
    public int OffsetPercent { get; set; }
}
