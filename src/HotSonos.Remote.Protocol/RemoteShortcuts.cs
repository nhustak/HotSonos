namespace HotSonos.Remote.Protocol;

/// <summary>Keyboard and media-key commands for the phone remote. Text fields do not take these except real media keys.</summary>
public enum RemoteShortcut
{
    PlayPause,
    Previous,
    Next,
    VolumeUp,
    VolumeDown,
    Mute,
}

public static class RemoteShortcuts
{
    /// <summary>
    /// Map a key token to a command.
    /// <paramref name="typing"/> is true while an editor has focus: letters, space, arrows, and +/- stay with the field.
    /// Media keys still apply. <paramref name="chord"/> (Ctrl, Alt, Meta) keeps letter keys for the platform.
    /// Volume hardware keys are not tokens here; the activity handles those on their own.
    /// </summary>
    public static bool TryMatch(string? key, bool shift, bool typing, bool chord, out RemoteShortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrEmpty(key))
            return false;

        var media = IsMedia(key);
        if ((typing || chord) && !media)
            return false;

        switch (key)
        {
            case "Space":
            case "MediaPlayPause":
                shortcut = RemoteShortcut.PlayPause;
                return true;
            case "Left":
            case "MediaPrevious":
                shortcut = RemoteShortcut.Previous;
                return true;
            case "Right":
            case "MediaNext":
                shortcut = RemoteShortcut.Next;
                return true;
            case "Up":
            case "Plus":
                shortcut = RemoteShortcut.VolumeUp;
                return true;
            case "Equals" when shift:
                shortcut = RemoteShortcut.VolumeUp;
                return true;
            case "Down":
            case "Minus":
                shortcut = RemoteShortcut.VolumeDown;
                return true;
            case "M":
            case "MediaMute":
                shortcut = RemoteShortcut.Mute;
                return true;
            default:
                return false;
        }
    }

    public static bool IsMedia(string key) =>
        key.StartsWith("Media", StringComparison.Ordinal);
}
