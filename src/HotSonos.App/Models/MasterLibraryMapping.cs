namespace HotSonos.App.Models;

/// <summary>
/// Associates a master (hi-res) archive root with a Sonos library path prefix.
/// Tracks under <see cref="SonosPath"/> dual-write tags to twins under <see cref="MasterRoot"/>.
/// Paths with no matching mapping are Sonos-only (no master dual-write).
/// </summary>
public sealed class MasterLibraryMapping
{
    /// <summary>Sonos library root or subfolder (UNC/local). Prefix match on track paths.</summary>
    public string SonosPath { get; set; } = "";

    /// <summary>Master archive root for dual-write for tracks under <see cref="SonosPath"/>.</summary>
    public string MasterRoot { get; set; } = "";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(SonosPath) && string.IsNullOrWhiteSpace(MasterRoot)
            ? "(empty)"
            : $"{SonosPath}  →  {MasterRoot}";
}
