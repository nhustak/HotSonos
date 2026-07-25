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

    // ---- Wake to music ----------------------------------------------------

    /// <summary>Bitmask: bit n = <see cref="DayOfWeek"/> n (0=Sunday). Default Mon–Fri.</summary>
    public const int DefaultWakeDaysMask = 0b0111110; // bits 1–5

    public const string WakeSourceShuffle = "Shuffle";
    public const string WakeSourceFavorite = "Favorite";

    /// <summary>Scheduled wake-to-music alarm (PC must be awake; HotSonos running).</summary>
    public bool WakeEnabled { get; set; }

    /// <summary>Wake clock time as minutes since midnight (e.g. 420 = 07:00).</summary>
    public int WakeMinutes { get; set; } = 7 * 60;

    /// <summary>Days the wake may fire; see <see cref="DefaultWakeDaysMask"/>.</summary>
    public int WakeDaysMask { get; set; } = DefaultWakeDaysMask;

    /// <summary>Room (coordinator room name) where wake starts; null uses active room at fire time.</summary>
    public string? WakeRoom { get; set; }

    /// <summary><see cref="WakeSourceShuffle"/> or <see cref="WakeSourceFavorite"/>.</summary>
    public string WakeSource { get; set; } = WakeSourceShuffle;

    /// <summary>Favorite/playlist title when <see cref="WakeSource"/> is Favorite.</summary>
    public string? WakeFavoriteName { get; set; }

    public int WakeStartVolume { get; set; } = 5;
    public int WakeEndVolume { get; set; } = 35;
    public int WakeVolumeStep { get; set; } = 2;
    public int WakeStepIntervalMinutes { get; set; } = 1;

    /// <summary>After ramp completes: join all speakers and shuffle the full library.</summary>
    public bool WakeExpandToHouse { get; set; } = true;

    // ---- MCP (loopback agent access while the app is running) -------------

    /// <summary>Host an HTTP MCP server on 127.0.0.1 for AI debug/control tools.</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>Loopback port for MCP (default 42341). Endpoint: http://127.0.0.1:{port}/mcp</summary>
    public int McpPort { get; set; } = 42341;

    // ---- Music library roots (filesystem; for future scan/tags; not Sonos UPnP) ----

    /// <summary>
    /// Local or UNC folder(s) that match Sonos Music Library share(s) — FLAC/MP3 playable set.
    /// Used by future library index/tag tools; daily shuffle still uses Sonos <c>A:TRACKS</c> until scoped.
    /// </summary>
    public List<string> SonosLibraryRoots { get; set; } = [];

    /// <summary>
    /// Optional full archive root (may include hi-res files not in Sonos). Tags dual-write here when a twin is matched/linked.
    /// </summary>
    public string? MasterLibraryRoot { get; set; }

    // ---- Tags (flat catalog; keys on files, labels in settings) ------------

    /// <summary>
    /// User tag catalog. Files store only <see cref="TagDefinition.Key"/> in <c>HOTSONOS_TAGS</c>;
    /// labels live here and can be renamed without rewriting files.
    /// </summary>
    public List<TagDefinition> Tags { get; set; } = [];

    /// <summary>Global hotkey that opens the quick-tag overlay for the playing track.</summary>
    public HotkeyConfig QuickTag { get; set; } = new();

    /// <summary>When true, tag writes also dual-write to master when linked.</summary>
    public bool TagUpdateMasterDefault { get; set; } = true;

    // ---- Library shuffle / play history ------------------------------------

    /// <summary>Tracks put on the Sonos queue when you shuffle (short = rebuilds more often).</summary>
    public int ShuffleQueueTracks { get; set; } = 80;

    /// <summary>Tracks appended when the queue is nearly empty (auto top-up).</summary>
    public int ShuffleTopUpTracks { get; set; } = 60;

    /// <summary>Days to remember actually-played tracks and hard-exclude them from new batches.</summary>
    public int ShuffleHistoryDays { get; set; } = 14;

    /// <summary>Auto top-up when this many tracks or fewer remain in the queue (needs Sonos GENA track counts).</summary>
    public int ShuffleTopUpWhenRemaining { get; set; } = 4;

    /// <summary>Hard-exclude tracks that were actually played within history days.</summary>
    public bool ShuffleExcludePlayed { get; set; } = true;

    /// <summary>When near the end of the queue, append another random batch automatically.</summary>
    public bool ShuffleAutoTopUp { get; set; } = true;

    /// <summary>Prefer not placing the same artist back-to-back when building a batch.</summary>
    public bool ShuffleArtistSpread { get; set; } = true;

    /// <summary>Exactly four favorite slots (see <see cref="EnsureShape"/>).</summary>
    public List<FavoriteSlot> FavoriteSlots { get; set; } = [];

    public const int FavoriteSlotCount = 4;

    /// <summary>True when <paramref name="day"/> is selected in <see cref="WakeDaysMask"/>.</summary>
    public bool WakeIncludesDay(DayOfWeek day) => (WakeDaysMask & (1 << (int)day)) != 0;

    public void SetWakeDay(DayOfWeek day, bool included)
    {
        var bit = 1 << (int)day;
        if (included) WakeDaysMask |= bit;
        else WakeDaysMask &= ~bit;
    }

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
            HotsonosAction.QuickTag => QuickTag,
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
        QuickTag ??= new HotkeyConfig();
        // First-time seed so existing installs get a usable quick-tag hotkey.
        if (!QuickTag.IsSet)
            QuickTag = new HotkeyConfig { Control = true, Alt = true, Key = "T" };
        if (VolumeStep < 1) VolumeStep = 5;
        if (LevelVolumePercent is < 0 or > 100) LevelVolumePercent = 20;
        if (NightlyResetMinutes is < 0 or > 1439) NightlyResetMinutes = 180;
        if (WakeMinutes is < 0 or > 1439) WakeMinutes = 7 * 60;
        if (WakeDaysMask is < 0 or > 0b1111111) WakeDaysMask = DefaultWakeDaysMask;
        if (WakeStartVolume is < 0 or > 100) WakeStartVolume = 5;
        if (WakeEndVolume is < 0 or > 100) WakeEndVolume = 35;
        if (WakeVolumeStep < 1) WakeVolumeStep = 2;
        if (WakeVolumeStep > 100) WakeVolumeStep = 100;
        if (WakeStepIntervalMinutes < 1) WakeStepIntervalMinutes = 1;
        if (WakeStepIntervalMinutes > 120) WakeStepIntervalMinutes = 120;
        if (McpPort is < 1024 or > 65535) McpPort = 42341;
        if (!string.Equals(WakeSource, WakeSourceFavorite, StringComparison.OrdinalIgnoreCase))
            WakeSource = WakeSourceShuffle;
        else
            WakeSource = WakeSourceFavorite;
        SonosLibraryRoots ??= [];
        SonosLibraryRoots = SonosLibraryRoots
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        MasterLibraryRoot = string.IsNullOrWhiteSpace(MasterLibraryRoot)
            ? null
            : MasterLibraryRoot.Trim();
        if (ShuffleQueueTracks is < 20 or > 500) ShuffleQueueTracks = 80;
        if (ShuffleTopUpTracks is < 10 or > 300) ShuffleTopUpTracks = 60;
        if (ShuffleHistoryDays is < 1 or > 90) ShuffleHistoryDays = 14;
        if (ShuffleTopUpWhenRemaining is < 1 or > 30) ShuffleTopUpWhenRemaining = 4;
        FavoriteSlots ??= [];
        while (FavoriteSlots.Count < FavoriteSlotCount)
            FavoriteSlots.Add(new FavoriteSlot());
        if (FavoriteSlots.Count > FavoriteSlotCount)
            FavoriteSlots = FavoriteSlots.Take(FavoriteSlotCount).ToList();

        EnsureTagCatalog();
        return this;
    }

    /// <summary>Normalizes the flat tag catalog; seeds starter tags when empty.</summary>
    public void EnsureTagCatalog()
    {
        Tags ??= [];

        // Drop invalid entries; generate keys if somehow missing.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<TagDefinition>();
        foreach (var t in Tags)
        {
            var label = (t.Label ?? "").Trim();
            if (label.Length == 0)
                continue;
            var key = (t.Key ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0 || !IsValidTagKey(key) || used.Contains(key))
                key = NewTagKey(used);
            used.Add(key);
            cleaned.Add(new TagDefinition { Key = key, Label = label });
        }

        Tags = cleaned;

        if (Tags.Count == 0)
        {
            // Starter set only — all are plain tags (no kinds). User can rename/delete.
            foreach (var label in new[] { "Slow", "Medium", "Fast", "Dinner", "Drive", "Focus" })
                Tags.Add(new TagDefinition { Key = NewTagKey(used), Label = label });
        }
    }

    /// <summary>Add a tag with a fresh auto key. Returns null if label empty.</summary>
    public TagDefinition? AddTag(string label)
    {
        label = (label ?? "").Trim();
        if (label.Length == 0)
            return null;
        EnsureTagCatalog();
        var used = new HashSet<string>(Tags.Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
        var tag = new TagDefinition { Key = NewTagKey(used), Label = label };
        Tags.Add(tag);
        return tag;
    }

    /// <summary>Rename by key; files unchanged. Returns false if key unknown or label empty.</summary>
    public bool RenameTag(string key, string newLabel)
    {
        newLabel = (newLabel ?? "").Trim();
        if (newLabel.Length == 0 || string.IsNullOrWhiteSpace(key))
            return false;
        EnsureTagCatalog();
        var tag = Tags.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        if (tag is null)
            return false;
        tag.Label = newLabel;
        return true;
    }

    /// <summary>Remove from catalog only (file keys remain until rewritten).</summary>
    public bool RemoveTag(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;
        EnsureTagCatalog();
        var n = Tags.RemoveAll(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        return n > 0;
    }

    /// <summary>Reorder catalog (affects Library / quick-tag chip order and keys 1–9).</summary>
    public bool MoveTag(string key, int delta)
    {
        if (string.IsNullOrWhiteSpace(key) || delta == 0)
            return false;
        EnsureTagCatalog();
        var i = Tags.FindIndex(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            return false;
        var j = i + delta;
        if (j < 0 || j >= Tags.Count)
            return false;
        (Tags[i], Tags[j]) = (Tags[j], Tags[i]);
        return true;
    }

    public TagDefinition? FindTag(string key) =>
        Tags.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    public string TagLabel(string key) =>
        FindTag(key)?.Label ?? "";

    /// <summary>
    /// Map a token to a catalog key: exact key, else label match (e.g. "medium" → Medium's id).
    /// Returns null if unknown (caller drops it — no tempo/legacy leftovers in UI).
    /// </summary>
    public string? ResolveTagToken(string? token)
    {
        token = (token ?? "").Trim();
        if (token.Length == 0)
            return null;
        EnsureTagCatalog();
        var byKey = FindTag(token);
        if (byKey is not null)
            return byKey.Key;
        var byLabel = Tags.FirstOrDefault(t =>
            string.Equals(t.Label, token, StringComparison.OrdinalIgnoreCase));
        return byLabel?.Key;
    }

    /// <summary>Rewrite mixed key/label tokens to catalog keys; drop unknowns.</summary>
    public List<string> NormalizeTagKeys(IEnumerable<string>? tokens)
    {
        EnsureTagCatalog();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tokens is null)
            return result;
        foreach (var raw in tokens)
        {
            var key = ResolveTagToken(raw);
            if (key is null || !seen.Add(key))
                continue;
            result.Add(key);
        }

        return result;
    }

    /// <summary>8-char lowercase hex; unique within <paramref name="used"/>.</summary>
    public static string NewTagKey(ISet<string>? used = null)
    {
        used ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 32; i++)
        {
            var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4))
                .ToLowerInvariant();
            if (!used.Contains(key))
            {
                used.Add(key);
                return key;
            }
        }

        // Extremely unlikely fallback
        var fallback = Guid.NewGuid().ToString("N")[..8];
        used.Add(fallback);
        return fallback;
    }

    public static bool IsValidTagKey(string key) =>
        key.Length is >= 4 and <= 32
        && key.All(c => char.IsAsciiLetterOrDigit(c));

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
        QuickTag = new HotkeyConfig { Control = true, Alt = true, Key = "T" },
    }.EnsureShape();
}
