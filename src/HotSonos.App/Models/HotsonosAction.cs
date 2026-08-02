namespace HotSonos.App.Models;

/// <summary>The actions a global hotkey (or tray click) can trigger.</summary>
public enum HotsonosAction
{
    PlayPause,
    Next,
    Previous,
    ShuffleLibrary,
    VolumeUp,
    VolumeDown,
    Mute,
    LevelVolumes,
    FreshStart,
    /// <summary>Open the quick-tag overlay (HotLaunch-style presets for the playing track).</summary>
    QuickTag,
    /// <summary>Open the quick-play overlay (1 = library shuffle, 2–9 = tags &amp; Sonos playlists).</summary>
    QuickPlay,
    /// <summary>Run hard failure diagnostic (ping LAN/NAS/speakers, topology, now-playing) and save report.</summary>
    FailureDiagnostic,
    Favorite1,
    Favorite2,
    Favorite3,
    Favorite4,
    Favorite5,
    Favorite6,
}

public static class HotsonosActionExtensions
{
    /// <summary>Zero-based favorite-slot index for the Favorite1..6 actions, else -1.</summary>
    public static int FavoriteSlotIndex(this HotsonosAction action) => action switch
    {
        HotsonosAction.Favorite1 => 0,
        HotsonosAction.Favorite2 => 1,
        HotsonosAction.Favorite3 => 2,
        HotsonosAction.Favorite4 => 3,
        HotsonosAction.Favorite5 => 4,
        HotsonosAction.Favorite6 => 5,
        _ => -1,
    };
}
