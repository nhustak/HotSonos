namespace HotSonos.App.Models;

/// <summary>One of the four "play a specific favorite/playlist" hotkey slots.</summary>
public sealed class FavoriteSlot
{
    /// <summary>Title of the Sonos favorite/playlist to play; null when unassigned.</summary>
    public string? FavoriteName { get; set; }

    public HotkeyConfig Hotkey { get; set; } = new();
}

/// <summary>
/// Persisted HotSonos configuration (JSON at %LocalAppData%\HotSonos\settings.json).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Room/group the hotkeys target. Null until first discovery resolves one.</summary>
    public string? ActiveRoom { get; set; }

    /// <summary>Pop the Now-Playing flyout on every track change.</summary>
    public bool ShowFlyoutOnTrackChange { get; set; } = true;

    /// <summary>Pop the Now-Playing flyout when you trigger an action (skip/volume/etc.).</summary>
    public bool ShowFlyoutOnAction { get; set; } = true;

    /// <summary>Keep the flyout on-screen always (updates live).</summary>
    public bool FlyoutPinned { get; set; }

    /// <summary>Persisted flyout position; null until the user drags it.</summary>
    public double? FlyoutLeft { get; set; }
    public double? FlyoutTop { get; set; }

    /// <summary>Persisted Settings-window geometry; null until the user moves/resizes it.</summary>
    public double? MainWindowLeft { get; set; }
    public double? MainWindowTop { get; set; }
    public double? MainWindowWidth { get; set; }
    public double? MainWindowHeight { get; set; }

    public HotkeyConfig PlayPause { get; set; } = new();
    public HotkeyConfig Next { get; set; } = new();
    public HotkeyConfig Previous { get; set; } = new();

    /// <summary>Shuffle the entire local Music Library — the primary action.</summary>
    public HotkeyConfig ShuffleLibrary { get; set; } = new();

    public HotkeyConfig VolumeUp { get; set; } = new();
    public HotkeyConfig VolumeDown { get; set; } = new();
    public HotkeyConfig Mute { get; set; } = new();

    /// <summary>Re-discover, regroup all speakers, and fresh-shuffle the library.</summary>
    public HotkeyConfig FreshStart { get; set; } = new();

    /// <summary>Percent the group volume changes per Volume Up/Down press.</summary>
    public int VolumeStep { get; set; } = 5;

    /// <summary>Set every speaker to this absolute volume when "level all volumes" runs.</summary>
    public int LevelVolumePercent { get; set; } = 20;

    /// <summary>Hotkey to set all speakers to <see cref="LevelVolumePercent"/>.</summary>
    public HotkeyConfig LevelVolumes { get; set; } = new();

    /// <summary>Silently regroup all speakers once a night (skipped if anything is playing).</summary>
    public bool NightlyResetEnabled { get; set; } = true;

    /// <summary>Time of the nightly reset, as minutes since midnight (default 180 = 3:00 AM).</summary>
    public int NightlyResetMinutes { get; set; } = 180;

    /// <summary>Also reshuffle (starts playback) after the nightly regroup, instead of only regrouping silently.</summary>
    public bool NightlyResetReshuffle { get; set; }

    /// <summary>Exactly four favorite slots (see <see cref="EnsureShape"/>).</summary>
    public List<FavoriteSlot> FavoriteSlots { get; set; } = [];

    public const int FavoriteSlotCount = 4;

    /// <summary>Returns the hotkey configured for <paramref name="action"/>.</summary>
    public HotkeyConfig HotkeyFor(HotsonosAction action)
    {
        var slot = action.FavoriteSlotIndex();
        if (slot >= 0)
            return FavoriteSlots[slot].Hotkey;

        return action switch
        {
            HotsonosAction.PlayPause => PlayPause,
            HotsonosAction.Next => Next,
            HotsonosAction.Previous => Previous,
            HotsonosAction.ShuffleLibrary => ShuffleLibrary,
            HotsonosAction.VolumeUp => VolumeUp,
            HotsonosAction.VolumeDown => VolumeDown,
            HotsonosAction.Mute => Mute,
            HotsonosAction.LevelVolumes => LevelVolumes,
            HotsonosAction.FreshStart => FreshStart,
            _ => new HotkeyConfig(),
        };
    }

    /// <summary>Guarantees there are exactly four favorite slots after load/default.</summary>
    public AppSettings EnsureShape()
    {
        PlayPause ??= new HotkeyConfig();
        Next ??= new HotkeyConfig();
        Previous ??= new HotkeyConfig();
        ShuffleLibrary ??= new HotkeyConfig();
        VolumeUp ??= new HotkeyConfig();
        VolumeDown ??= new HotkeyConfig();
        Mute ??= new HotkeyConfig();
        LevelVolumes ??= new HotkeyConfig();
        FreshStart ??= new HotkeyConfig();
        if (VolumeStep < 1) VolumeStep = 5;
        if (LevelVolumePercent is < 0 or > 100) LevelVolumePercent = 20;
        if (NightlyResetMinutes is < 0 or > 1439) NightlyResetMinutes = 180;
        FavoriteSlots ??= [];
        while (FavoriteSlots.Count < FavoriteSlotCount)
            FavoriteSlots.Add(new FavoriteSlot());
        if (FavoriteSlots.Count > FavoriteSlotCount)
            FavoriteSlots = FavoriteSlots.Take(FavoriteSlotCount).ToList();
        return this;
    }

    /// <summary>Sensible first-run defaults: Ctrl+Alt chords that rarely collide.</summary>
    public static AppSettings CreateDefault() => new AppSettings
    {
        VolumeStep = 5,
        ShuffleLibrary = new HotkeyConfig { Control = true, Alt = true, Key = "F8" },
        PlayPause = new HotkeyConfig { Control = true, Alt = true, Key = "F9" },
        Previous = new HotkeyConfig { Control = true, Alt = true, Key = "F10" },
        Next = new HotkeyConfig { Control = true, Alt = true, Key = "F11" },
        VolumeUp = new HotkeyConfig { Control = true, Alt = true, Key = "Up" },
        VolumeDown = new HotkeyConfig { Control = true, Alt = true, Key = "Down" },
        Mute = new HotkeyConfig { Control = true, Alt = true, Key = "M" },
    }.EnsureShape();
}
