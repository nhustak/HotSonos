namespace HotSonos.App.Models;

/// <summary>The actions a global hotkey (or tray click) can trigger.</summary>
public enum HotsonosAction
{
    PlayPause,
    Next,
    Previous,
    ShuffleLibrary,
    Favorite1,
    Favorite2,
    Favorite3,
    Favorite4,
}

public static class HotsonosActionExtensions
{
    /// <summary>Zero-based favorite-slot index for the Favorite1..4 actions, else -1.</summary>
    public static int FavoriteSlotIndex(this HotsonosAction action) => action switch
    {
        HotsonosAction.Favorite1 => 0,
        HotsonosAction.Favorite2 => 1,
        HotsonosAction.Favorite3 => 2,
        HotsonosAction.Favorite4 => 3,
        _ => -1,
    };
}
