namespace HotSonos.App.Models;

/// <summary>A named custom tag dimension stored in files as <see cref="StorageKey"/> (HOTSONOS_*).</summary>
public sealed class CustomTagDefinition
{
    /// <summary>Stable id for UI/MCP (e.g. mood, lane).</summary>
    public string Id { get; set; } = "";

    /// <summary>Display label.</summary>
    public string Label { get; set; } = "";

    /// <summary>File storage key (Vorbis / ID3 TXXX), e.g. HOTSONOS_LANE.</summary>
    public string StorageKey { get; set; } = "";

    /// <summary>When true, values may be ;-separated lists.</summary>
    public bool Multi { get; set; }
}

/// <summary>
/// One quick-tag slot (1–9): label shown in the overlay + fields to write.
/// Only keys present in <see cref="Set"/> are written (other HOTSONOS_* left alone).
/// </summary>
public sealed class TagPreset
{
    /// <summary>Slot number 1–9 (digit key in quick-tag overlay).</summary>
    public int Slot { get; set; }

    /// <summary>Short label for the overlay / menu.</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// Field map: storage key → value. Empty string clears the field.
    /// Keys should be HOTSONOS_* (e.g. HOTSONOS_TEMPO, HOTSONOS_LANE).
    /// </summary>
    public Dictionary<string, string> Set { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Summary =>
        Set.Count == 0
            ? "(empty)"
            : string.Join(", ", Set.Select(kv =>
                string.IsNullOrEmpty(kv.Value) ? $"{kv.Key}=∅" : $"{ShortKey(kv.Key)}={kv.Value}"));

    private static string ShortKey(string key) =>
        key.StartsWith("HOTSONOS_", StringComparison.OrdinalIgnoreCase)
            ? key["HOTSONOS_".Length..]
            : key;
}
