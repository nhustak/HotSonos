namespace HotSonos.App.Models;

/// <summary>One of the play-source hotkey slots (Sonos favorite/playlist, tag, genre, or library folder).</summary>
public sealed class FavoriteSlot
{
    public const string SourceSonos = "sonos";
    public const string SourceTag = "tag";
    public const string SourceGenre = "genre";
    public const string SourceFolder = "folder";

    /// <summary><see cref="SourceSonos"/>, <see cref="SourceTag"/>, <see cref="SourceGenre"/>, or <see cref="SourceFolder"/>.</summary>
    public string Source { get; set; } = SourceSonos;

    /// <summary>Title of the Sonos favorite/playlist when <see cref="Source"/> is sonos.</summary>
    public string? FavoriteName { get; set; }

    /// <summary>Catalog tag key when <see cref="Source"/> is tag.</summary>
    public string? TagKey { get; set; }

    /// <summary>Standard Genre field label when <see cref="Source"/> is genre.</summary>
    public string? GenreName { get; set; }

    /// <summary>Library folder UNC path when <see cref="Source"/> is folder.</summary>
    public string? FolderPath { get; set; }

    public HotkeyConfig Hotkey { get; set; } = new();

    public bool IsTag =>
        string.Equals(Source, SourceTag, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(TagKey);

    public bool IsGenre =>
        string.Equals(Source, SourceGenre, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(GenreName);

    public bool IsFolder =>
        string.Equals(Source, SourceFolder, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(FolderPath);

    public bool IsSonos =>
        !IsTag && !IsGenre && !IsFolder && !string.IsNullOrWhiteSpace(FavoriteName);

    public bool IsSet => IsTag || IsGenre || IsFolder || IsSonos;

    /// <summary>Tray / UI label for this slot.</summary>
    public string DisplayLabel(AppSettings? settings = null)
    {
        if (IsTag)
        {
            var label = settings?.FindTag(TagKey!)?.Label ?? TagKey;
            return $"Tag · {label}";
        }

        if (IsGenre)
            return $"Genre · {GenreName}";

        if (IsFolder)
        {
            var name = System.IO.Path.GetFileName(FolderPath!.TrimEnd('\\', '/'));
            return string.IsNullOrWhiteSpace(name) ? $"Folder · {FolderPath}" : $"Folder · {name}";
        }

        if (IsSonos)
            return FavoriteName!;

        return "(unset)";
    }
}

/// <summary>
/// Persisted HotSonos configuration (JSON at %LocalAppData%\HotSonos\settings.json).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Room/group the hotkeys target. Null until first discovery resolves one.</summary>
    public string? ActiveRoom { get; set; }

    /// <summary>
    /// Preferred whole-house group coordinator (room name). When set, shuffle / fresh start /
    /// regroup joins every speaker to this room so it leads the group. Null = current active group.
    /// </summary>
    public string? PreferredHouseCoordinatorRoom { get; set; }

    /// <summary>
    /// When true (default), Daily shuffle / Fresh Start / house regroup join every visible speaker.
    /// When false, only rooms in <see cref="DailyGroupRooms"/> join (and others are left out of the group).
    /// </summary>
    public bool DailyGroupAllSpeakers { get; set; } = true;

    /// <summary>
    /// Room names included in the Daily house group when <see cref="DailyGroupAllSpeakers"/> is false.
    /// Empty while not-all is treated as all (safe fallback). Prefer keeping the preferred coordinator checked.
    /// </summary>
    public List<string> DailyGroupRooms { get; set; } = [];

    /// <summary>
    /// Rooms allowed in the Daily group, or null when every speaker should join.
    /// </summary>
    public IReadOnlySet<string>? GetDailyGroupRoomAllowList()
    {
        if (DailyGroupAllSpeakers)
            return null;
        DailyGroupRooms ??= [];
        var set = DailyGroupRooms
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Empty selection must not strand the house — fall back to all.
        return set.Count == 0 ? null : set;
    }

    public bool IsDailyGroupRoom(string? roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            return false;
        var allow = GetDailyGroupRoomAllowList();
        return allow is null || allow.Contains(roomName.Trim());
    }

    /// <summary>Pop the Now-Playing flyout on every track change.</summary>
    public bool ShowFlyoutOnTrackChange { get; set; } = true;

    /// <summary>Pop the Now-Playing flyout when you trigger an action (skip/volume/etc.).</summary>
    public bool ShowFlyoutOnAction { get; set; } = true;

    /// <summary>
    /// Tray icon double-click: <see cref="TrayDoubleClickShuffle"/>, <see cref="TrayDoubleClickControl"/>, or <see cref="TrayDoubleClickLibrary"/>.
    /// </summary>
    public string TrayDoubleClickAction { get; set; } = TrayDoubleClickShuffle;

    public const string TrayDoubleClickShuffle = "shuffle";
    public const string TrayDoubleClickControl = "control";
    public const string TrayDoubleClickLibrary = "library";

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

    /// <summary>
    /// Per-room offsets applied when writing house logical levels (Level all, Wake,
    /// volume ±). Example: Theater +60 so logical 20% → Port raw 80% (amp calibration).
    /// Rooms with a non-zero offset are excluded from the house primary % used for ±
    /// toasts (median of offset-0 rooms).
    /// </summary>
    public List<RoomVolumeOffset> RoomVolumeOffsets { get; set; } = [];

    /// <summary>Hotkey to set all speakers to <see cref="LevelVolumePercent"/>.</summary>
    public HotkeyConfig LevelVolumes { get; set; } = new();

    /// <summary>Offset for a room name (0 if unset).</summary>
    public int GetVolumeOffset(string? roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName) || RoomVolumeOffsets is null || RoomVolumeOffsets.Count == 0)
            return 0;
        var hit = RoomVolumeOffsets.FirstOrDefault(o =>
            string.Equals(o.RoomName, roomName.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit?.OffsetPercent ?? 0;
    }

    /// <summary>Logical house level + room offset, clamped 0–100.</summary>
    public int ApplyVolumeOffset(string? roomName, int logicalPercent) =>
        Math.Clamp(logicalPercent + GetVolumeOffset(roomName), 0, 100);

    public void SetVolumeOffset(string roomName, int offsetPercent)
    {
        roomName = (roomName ?? "").Trim();
        if (roomName.Length == 0) return;
        offsetPercent = Math.Clamp(offsetPercent, -100, 100);
        RoomVolumeOffsets ??= [];
        var existing = RoomVolumeOffsets.FirstOrDefault(o =>
            string.Equals(o.RoomName, roomName, StringComparison.OrdinalIgnoreCase));
        if (offsetPercent == 0)
        {
            if (existing is not null)
                RoomVolumeOffsets.Remove(existing);
            return;
        }

        if (existing is not null)
            existing.OffsetPercent = offsetPercent;
        else
            RoomVolumeOffsets.Add(new RoomVolumeOffset { RoomName = roomName, OffsetPercent = offsetPercent });
    }

    /// <summary>
    /// When the coordinator hits ERROR_* or unexpected STOPPED after playing, try Next/Play
    /// and if still dead rebuild library shuffle. Default on — recovers queue/resource failures.
    /// Does not resume a deliberate Pause.
    /// </summary>
    public bool AutoRecoverPlayback { get; set; } = true;

    /// <summary>
    /// UTC ISO timestamp when one-shot legacy tag/tempo migration finished successfully.
    /// When set, startup skips rewriting every tagged file (was thrashing NAS + TagLib every launch).
    /// </summary>
    public string? LegacyTagMigrationCompletedUtc { get; set; }

    /// <summary>
    /// Full topology monitor (bonded Sub parse, event JSONL, Topology map diffs).
    /// Default <c>false</c> — GENA floods + file I/O delayed volume. Toggle on Topology tab only when debugging.
    /// </summary>
    public bool TopologyMonitorEnabled { get; set; }

    /// <summary>
    /// When true, if rooms peel into extra groups while still online, auto-regroup under
    /// the active coordinator (debounced + cooldown). Default <c>false</c> — regroup storms
    /// delay audio; use Topology → Regroup all, or enable only when hunting flaky rooms.
    /// </summary>
    public bool KeepHouseGrouped { get; set; }

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
    /// Used by library index/tag tools and Discover. May include Jazz, Christmas, Sonos, etc.
    /// </summary>
    public List<string> SonosLibraryRoots { get; set; } = [];

    /// <summary>
    /// Folders included in Daily / “All · Music Library” shuffle (hotkey, Control From All, top-up).
    /// Subset of (or equal to) <see cref="SonosLibraryRoots"/>. Empty = all configured roots
    /// (single-root installs stay simple). Prefer only the resampled daily tree (e.g. …\Sonos).
    /// </summary>
    public List<string> DailyLibraryRoots { get; set; } = [];

    /// <summary>
    /// Master (hi-res) archive roots associated with a Sonos path prefix.
    /// Dual-write only runs when a track is under a mapping's <see cref="MasterLibraryMapping.SonosPath"/>.
    /// Christmas / mood folders can omit a mapping (Sonos-only).
    /// </summary>
    public List<MasterLibraryMapping> MasterLibraryMappings { get; set; } = [];

    /// <summary>
    /// Legacy single master root. Migrated into <see cref="MasterLibraryMappings"/> on load
    /// (one entry per Sonos library root). Prefer mappings for new config.
    /// </summary>
    public string? MasterLibraryRoot { get; set; }

    /// <summary>
    /// Folders used for Daily / All library shuffle. Non-empty <see cref="DailyLibraryRoots"/>
    /// when set; otherwise all <see cref="SonosLibraryRoots"/>.
    /// </summary>
    public IReadOnlyList<string> GetEffectiveDailyLibraryRoots()
    {
        if (DailyLibraryRoots.Count > 0)
            return DailyLibraryRoots;
        return SonosLibraryRoots;
    }

    /// <summary>
    /// Path prefixes for shuffle include-filter, or null when the whole Sonos library applies
    /// (no daily restriction / daily equals every configured root).
    /// </summary>
    public IReadOnlyList<string>? GetDailyShuffleIncludePrefixes()
    {
        var daily = GetEffectiveDailyLibraryRoots();
        if (daily.Count == 0)
            return null;

        if (SonosLibraryRoots.Count > 0
            && daily.Count >= SonosLibraryRoots.Count
            && SonosLibraryRoots.All(r =>
                daily.Any(d => string.Equals(d, r, StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }

        return daily;
    }

    /// <summary>
    /// Resolve the master archive root for a Sonos file or folder path.
    /// Longest matching <see cref="MasterLibraryMapping.SonosPath"/> prefix wins.
    /// </summary>
    public string? ResolveMasterRootForSonosPath(string? sonosFileOrFolder)
    {
        if (string.IsNullOrWhiteSpace(sonosFileOrFolder))
            return null;

        var path = sonosFileOrFolder.Trim().TrimEnd('\\', '/');
        MasterLibraryMapping? best = null;
        var bestLen = -1;
        foreach (var m in MasterLibraryMappings)
        {
            if (string.IsNullOrWhiteSpace(m.SonosPath) || string.IsNullOrWhiteSpace(m.MasterRoot))
                continue;
            var prefix = m.SonosPath.Trim().TrimEnd('\\', '/');
            if (prefix.Length == 0) continue;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            // Require boundary: exact match or next char is separator
            if (path.Length > prefix.Length)
            {
                var next = path[prefix.Length];
                if (next is not '\\' and not '/')
                    continue;
            }

            if (prefix.Length > bestLen)
            {
                bestLen = prefix.Length;
                best = m;
            }
        }

        return best?.MasterRoot;
    }

    /// <summary>Distinct configured master roots (for status / UI).</summary>
    public IReadOnlyList<string> ListMasterRoots() =>
        MasterLibraryMappings
            .Select(m => m.MasterRoot?.Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- Tags (flat catalog; keys on files, labels in settings) ------------

    /// <summary>
    /// User tag catalog. Files store only <see cref="TagDefinition.Key"/> in <c>HOTSONOS_TAGS</c>;
    /// labels live here and can be renamed without rewriting files.
    /// </summary>
    public List<TagDefinition> Tags { get; set; } = [];

    /// <summary>Global hotkey that opens the quick-tag overlay for the playing track.</summary>
    public HotkeyConfig QuickTag { get; set; } = new();

    /// <summary>Global hotkey that opens the quick-play overlay (shuffle + tags + Sonos playlists).</summary>
    public HotkeyConfig QuickPlay { get; set; } = new();

    /// <summary>Global hotkey that runs a hard failure diagnostic (LAN / NAS / Sonos) and saves a report.</summary>
    public HotkeyConfig FailureDiagnostic { get; set; } = new();

    /// <summary>When true, tag writes also dual-write to master when linked.</summary>
    public bool TagUpdateMasterDefault { get; set; } = true;

    // ---- Library shuffle / play history ------------------------------------

    /// <summary>Tracks put on the Sonos queue when you shuffle (short = rebuilds more often).</summary>
    public int ShuffleQueueTracks { get; set; } = 80;

    /// <summary>Tracks appended when the queue is nearly empty (auto top-up).</summary>
    public int ShuffleTopUpTracks { get; set; } = 60;

    /// <summary>Days to remember played/skipped tracks and hard-exclude them from new batches.</summary>
    public int ShuffleHistoryDays { get; set; } = 14;

    /// <summary>Auto top-up when this many tracks or fewer remain in the queue (needs Sonos GENA track counts).</summary>
    public int ShuffleTopUpWhenRemaining { get; set; } = 4;

    /// <summary>Hard-exclude tracks that were played or skipped within history days.</summary>
    public bool ShuffleExcludePlayed { get; set; } = true;

    /// <summary>When near the end of the queue, append another random batch automatically.</summary>
    public bool ShuffleAutoTopUp { get; set; } = true;

    /// <summary>
    /// After a one-shot track or tag queue (e.g. play Favs), when the queue runs low,
    /// auto top-up with full-library shuffle. Default true — “play this, then keep going.”
    /// </summary>
    public bool ContinueLibraryShuffleAfterSpecialPlay { get; set; } = true;

    /// <summary>Prefer not placing the same artist back-to-back when building a batch.</summary>
    public bool ShuffleArtistSpread { get; set; } = true;

    /// <summary>
    /// When true (default), standard Genre values appear in Control / Quick Play / favorite-slot
    /// pickers. Turn off for installs that only want All + HotSonos tags (+ Sonos).
    /// </summary>
    public bool ShowGenresInPlaySources { get; set; } = true;

    /// <summary>
    /// Control-tab shuffle picker: <c>all</c>, <c>folder:{path}</c>, <c>tag:{key}</c>, or <c>genre:{name}</c>.
    /// Hotkey shuffle remains Daily mix (All); this only affects the Start shuffle button.
    /// </summary>
    public string ControlShuffleSource { get; set; } = "all";

    public const string ControlShuffleAll = "all";
    public const string ControlShuffleFolderPrefix = "folder:";
    public const string ControlShuffleTagPrefix = "tag:";
    public const string ControlShuffleGenrePrefix = "genre:";

    public static string FolderShuffleToken(string folderPath) =>
        ControlShuffleFolderPrefix + (folderPath ?? "").Trim();

    public static bool TryParseFolderShuffleToken(string? token, out string folderPath)
    {
        folderPath = "";
        if (string.IsNullOrWhiteSpace(token)
            || !token.StartsWith(ControlShuffleFolderPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        folderPath = token[ControlShuffleFolderPrefix.Length..].Trim();
        return folderPath.Length > 0;
    }

    /// <summary>Exactly <see cref="FavoriteSlotCount"/> favorite slots (see <see cref="EnsureShape"/>).</summary>
    public List<FavoriteSlot> FavoriteSlots { get; set; } = [];

    public const int FavoriteSlotCount = 6;

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
            HotsonosAction.QuickPlay => QuickPlay,
            HotsonosAction.FailureDiagnostic => FailureDiagnostic,
            _ => new HotkeyConfig(),
        };
    }

    /// <summary>Guarantees there are exactly <see cref="FavoriteSlotCount"/> favorite slots after load/default.</summary>
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
        QuickPlay ??= new HotkeyConfig();
        FailureDiagnostic ??= new HotkeyConfig();
        // First-time seed so existing installs get usable overlay hotkeys.
        if (!QuickTag.IsSet)
            QuickTag = new HotkeyConfig { Control = true, Alt = true, Key = "T" };
        if (!QuickPlay.IsSet)
            QuickPlay = new HotkeyConfig { Control = true, Alt = true, Key = "P" };
        if (!FailureDiagnostic.IsSet)
            FailureDiagnostic = new HotkeyConfig { Control = true, Alt = true, Key = "D" };
        if (VolumeStep < 1) VolumeStep = 5;
        if (LevelVolumePercent is < 0 or > 100) LevelVolumePercent = 20;
        RoomVolumeOffsets ??= [];
        RoomVolumeOffsets = RoomVolumeOffsets
            .Where(o => o is not null && !string.IsNullOrWhiteSpace(o.RoomName))
            .Select(o => new RoomVolumeOffset
            {
                RoomName = o.RoomName.Trim(),
                OffsetPercent = Math.Clamp(o.OffsetPercent, -100, 100),
            })
            .GroupBy(o => o.RoomName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .Where(o => o.OffsetPercent != 0)
            .ToList();
        DailyGroupRooms ??= [];
        DailyGroupRooms = DailyGroupRooms
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Not-all with zero rooms → treat as all (avoid empty house by accident).
        if (!DailyGroupAllSpeakers && DailyGroupRooms.Count == 0)
            DailyGroupAllSpeakers = true;
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
        TrayDoubleClickAction = (TrayDoubleClickAction ?? "").Trim().ToLowerInvariant() switch
        {
            TrayDoubleClickControl => TrayDoubleClickControl,
            TrayDoubleClickLibrary => TrayDoubleClickLibrary,
            _ => TrayDoubleClickShuffle,
        };
        if (!string.Equals(WakeSource, WakeSourceFavorite, StringComparison.OrdinalIgnoreCase))
            WakeSource = WakeSourceShuffle;
        else
            WakeSource = WakeSourceFavorite;
        SonosLibraryRoots ??= [];
        SonosLibraryRoots = SonosLibraryRoots
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        DailyLibraryRoots ??= [];
        DailyLibraryRoots = DailyLibraryRoots
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => SonosLibraryRoots.Count == 0
                        || SonosLibraryRoots.Any(r =>
                            string.Equals(d, r, StringComparison.OrdinalIgnoreCase)
                            || d.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                            || r.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        // Multi-root and nothing marked daily yet → prefer folder(s) named "Sonos", else leave empty (= all).
        if (DailyLibraryRoots.Count == 0 && SonosLibraryRoots.Count > 1)
        {
            var sonosNamed = SonosLibraryRoots
                .Where(r => r.TrimEnd('\\', '/').EndsWith("\\Sonos", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(System.IO.Path.GetFileName(r.TrimEnd('\\', '/')), "Sonos",
                                StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sonosNamed.Count > 0)
                DailyLibraryRoots = sonosNamed;
        }

        MasterLibraryRoot = string.IsNullOrWhiteSpace(MasterLibraryRoot)
            ? null
            : MasterLibraryRoot.Trim();
        MasterLibraryMappings ??= [];
        MasterLibraryMappings = MasterLibraryMappings
            .Where(m => m is not null
                        && !string.IsNullOrWhiteSpace(m.SonosPath)
                        && !string.IsNullOrWhiteSpace(m.MasterRoot))
            .Select(m => new MasterLibraryMapping
            {
                SonosPath = m.SonosPath.Trim().TrimEnd('\\', '/'),
                MasterRoot = m.MasterRoot.Trim().TrimEnd('\\', '/'),
            })
            .GroupBy(m => m.SonosPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        // Legacy single MasterLibraryRoot → one mapping per Sonos library root.
        if (MasterLibraryMappings.Count == 0 && !string.IsNullOrWhiteSpace(MasterLibraryRoot))
        {
            if (SonosLibraryRoots.Count > 0)
            {
                foreach (var root in SonosLibraryRoots)
                {
                    MasterLibraryMappings.Add(new MasterLibraryMapping
                    {
                        SonosPath = root.TrimEnd('\\', '/'),
                        MasterRoot = MasterLibraryRoot,
                    });
                }
            }
            else
            {
                // No Sonos roots yet — keep a placeholder mapping with empty Sonos path rejected;
                // attach when roots appear: store master on first root later. For now map master
                // to itself as SonosPath so user still sees master until roots are discovered.
                // Better: leave mappings empty and keep MasterLibraryRoot until roots exist.
            }
        }

        // Keep legacy property in sync with first mapping for older readers / UI fallback.
        if (MasterLibraryMappings.Count > 0)
            MasterLibraryRoot = MasterLibraryMappings[0].MasterRoot;
        if (ShuffleQueueTracks is < 20 or > 500) ShuffleQueueTracks = 80;
        if (ShuffleTopUpTracks is < 10 or > 300) ShuffleTopUpTracks = 60;
        if (ShuffleHistoryDays is < 1 or > 90) ShuffleHistoryDays = 14;
        if (ShuffleTopUpWhenRemaining is < 1 or > 30) ShuffleTopUpWhenRemaining = 4;
        ControlShuffleSource = string.IsNullOrWhiteSpace(ControlShuffleSource)
            ? ControlShuffleAll
            : ControlShuffleSource.Trim();
        FavoriteSlots ??= [];
        while (FavoriteSlots.Count < FavoriteSlotCount)
            FavoriteSlots.Add(new FavoriteSlot());
        if (FavoriteSlots.Count > FavoriteSlotCount)
            FavoriteSlots = FavoriteSlots.Take(FavoriteSlotCount).ToList();

        EnsureTagCatalog();

        // Normalize favorite slots after tags exist (tag labels/keys).
        foreach (var slot in FavoriteSlots)
        {
            if (string.Equals(slot.Source, FavoriteSlot.SourceTag, StringComparison.OrdinalIgnoreCase))
                slot.Source = FavoriteSlot.SourceTag;
            else if (string.Equals(slot.Source, FavoriteSlot.SourceGenre, StringComparison.OrdinalIgnoreCase))
                slot.Source = FavoriteSlot.SourceGenre;
            else if (string.Equals(slot.Source, FavoriteSlot.SourceFolder, StringComparison.OrdinalIgnoreCase))
                slot.Source = FavoriteSlot.SourceFolder;
            else
                slot.Source = FavoriteSlot.SourceSonos;

            if (slot.IsFolder)
            {
                slot.FolderPath = string.IsNullOrWhiteSpace(slot.FolderPath)
                    ? null
                    : slot.FolderPath.Trim().TrimEnd('\\', '/');
                if (slot.FolderPath is null
                    || (SonosLibraryRoots.Count > 0
                        && !SonosLibraryRoots.Any(r =>
                            string.Equals(r, slot.FolderPath, StringComparison.OrdinalIgnoreCase)
                            || slot.FolderPath.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))))
                {
                    // Orphan / unknown folder — clear if roots are configured.
                    if (SonosLibraryRoots.Count > 0)
                    {
                        slot.FolderPath = null;
                        slot.Source = FavoriteSlot.SourceSonos;
                    }
                }
                else
                {
                    slot.TagKey = null;
                    slot.GenreName = null;
                    slot.FavoriteName = null;
                }
            }

            if (slot.IsTag)
            {
                var tag = FindTag(slot.TagKey!);
                if (tag is null)
                {
                    // Orphan tag key — clear binding.
                    slot.TagKey = null;
                    slot.Source = FavoriteSlot.SourceSonos;
                }
                else
                {
                    slot.TagKey = tag.Key;
                    slot.FavoriteName = null;
                    slot.GenreName = null;
                }
            }
            else if (slot.IsGenre)
            {
                slot.GenreName = slot.GenreName!.Trim();
                slot.TagKey = null;
                slot.FavoriteName = null;
            }
            else if (string.IsNullOrWhiteSpace(slot.FavoriteName))
            {
                slot.FavoriteName = null;
                slot.GenreName = null;
            }
            else
            {
                slot.TagKey = null;
                slot.GenreName = null;
            }
        }

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
        QuickPlay = new HotkeyConfig { Control = true, Alt = true, Key = "P" },
        FailureDiagnostic = new HotkeyConfig { Control = true, Alt = true, Key = "D" },
    }.EnsureShape();
}
