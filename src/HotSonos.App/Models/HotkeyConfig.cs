using System.Text.Json.Serialization;
using System.Windows.Input;

namespace HotSonos.App.Models;

/// <summary>
/// A serializable global-hotkey definition: modifier flags plus a single key,
/// stored by its <see cref="System.Windows.Input.Key"/> name (e.g. "F9", "P").
/// </summary>
public sealed class HotkeyConfig
{
    public bool Control { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    /// <summary>The non-modifier key name; empty when unset.</summary>
    public string Key { get; set; } = "";

    [JsonIgnore]
    public bool IsSet => !string.IsNullOrEmpty(Key);

    /// <summary>Win32 RegisterHotKey modifier mask (MOD_ALT/CONTROL/SHIFT/WIN).</summary>
    [JsonIgnore]
    public uint Modifiers =>
        (Alt ? 0x0001u : 0) | (Control ? 0x0002u : 0) | (Shift ? 0x0004u : 0) | (Win ? 0x0008u : 0);

    /// <summary>Win32 virtual-key code for <see cref="Key"/>, or 0 if unparseable.</summary>
    [JsonIgnore]
    public uint VirtualKey =>
        Enum.TryParse<Key>(Key, out var k) ? (uint)KeyInterop.VirtualKeyFromKey(k) : 0u;

    /// <summary>Human-readable chord, e.g. "Ctrl + Alt + P". Empty string when unset.</summary>
    public override string ToString()
    {
        if (!IsSet)
            return "";

        var parts = new List<string>(4);
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join(" + ", parts);
    }
}
