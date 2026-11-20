namespace HotSonos.Remote.Protocol;

public sealed record RemoteVolumesResponse(
    bool Ok,
    int? HouseVolume,
    bool? HouseMuted,
    IReadOnlyList<RemoteSpeakerVolumeDto> Speakers,
    string? Error);

public sealed record RemoteSpeakerVolumeDto(string Room, string Ip, int Volume, bool Muted, bool Reachable);

public sealed record RemoteTopologyResponse(
    bool Ok,
    string? ActiveRoom,
    string? PreferredCoordinator,
    bool KeepHouseGrouped,
    IReadOnlyList<RemoteGroupDto> Groups,
    IReadOnlyList<RemoteMemberDto> Members,
    IReadOnlyList<RemoteCoordinatorDto> Coordinators,
    IReadOnlyList<string> Offline,
    IReadOnlyList<string> Vanished,
    string? Error,
    string? LiveGrouping = null,
    string? DailyGroupPreset = null,
    bool? GroupedAsDaily = null,
    IReadOnlyList<RemoteTopologyGroupDto>? Layout = null);

/// <summary>One live group, or one room that is playing on its own. Bonded units are folded under a room.</summary>
public sealed record RemoteTopologyGroupDto(
    string GroupId,
    string CoordinatorRoom,
    bool Ungrouped,
    string? PlayState,
    IReadOnlyList<RemoteTopologyRoomDto> Rooms);

public sealed record RemoteTopologyRoomDto(
    string Room,
    bool IsCoordinator,
    IReadOnlyList<string> Folded);

public sealed record RemoteMemberDto(
    string Room,
    string Ip,
    string Uuid,
    bool Invisible,
    string? Role,
    string? Product,
    bool IsCoordinator,
    string? GroupId);

public sealed record RemoteCoordinatorDto(string Room, string Ip, bool IsLeading);

public sealed record RemoteDailyGroupsResponse(
    bool Ok,
    bool AllSpeakers,
    IReadOnlyList<string> Rooms,
    IReadOnlyList<string> KnownRooms,
    IReadOnlyList<RemotePresetDto> Presets,
    string? ActivePreset,
    string? Error);

public sealed record RemotePresetDto(string Name, IReadOnlyList<string> Rooms, bool Active);

public sealed record RemoteFavoritesResponse(bool Ok, IReadOnlyList<RemoteFavoriteDto> Favorites, string? Error);

public sealed record RemoteFavoriteDto(string Name, string Kind);

public sealed record RemoteSpotifyListResponse(bool Ok, bool Linked, IReadOnlyList<RemoteSpotifyDto> Links, string? Error);

public sealed record RemoteSpotifyDto(string Name, string Reference);

public sealed record RemoteLibraryStatusResponse(
    bool Ok,
    bool Scanning,
    int TrackCount,
    int Unplayable,
    string? Phase,
    string? LastError,
    string? Note);

public sealed record RemoteTagsResponse(bool Ok, IReadOnlyList<RemoteTagDto> Tags);

public sealed record RemoteSongStatDto(
    string TrackKey,
    string Title,
    string Artist,
    int PlayCount,
    int SkipCount,
    string LastPlayed);

public sealed record RemoteSongsResponse(bool Ok, IReadOnlyList<RemoteSongStatDto> Songs);

public sealed record RemotePlayRowDto(
    string TrackKey,
    string Title,
    string Artist,
    string WhenLocal,
    string Zone,
    string Outcome,
    string Trigger,
    string Listened,
    string Length);

public sealed record RemotePlaysResponse(bool Ok, IReadOnlyList<RemotePlayRowDto> Plays);

public sealed record RemoteRankDto(string Title, string Artist, int Count, string Label);

public sealed record RemoteMusicResponse(
    bool Ok,
    IReadOnlyList<RemotePlayRowDto> Plays,
    IReadOnlyList<RemoteRankDto> MostPlayed,
    IReadOnlyList<RemoteRankDto> MostSkipped,
    IReadOnlyList<RemoteRankDto> RecentlyPlayed);

public sealed record RemoteEventDto(long Id, string WhenLocal, string Category, string Kind, string Summary, string Zone);

public sealed record RemoteEventsResponse(bool Ok, IReadOnlyList<RemoteEventDto> Events, IReadOnlyList<string> Kinds);

public sealed record RemoteHeardResponse(bool Ok, IReadOnlyList<RemoteHeardDto> Tracks);

public sealed record RemoteHeardDto(string Title, string Artist, string Source, int Plays, string LastPlayed);

public sealed record RemoteLogsResponse(bool Ok, IReadOnlyList<string> Lines);

public sealed record RemoteSettingsResponse(
    bool Ok,
    int VolumeStep,
    int LevelVolumePercent,
    bool KeepHouseGrouped,
    bool NightlyResetEnabled,
    int NightlyResetMinutes,
    bool NightlyResetReshuffle,
    bool WakeEnabled,
    int WakeMinutes,
    int WakeDaysMask,
    string? WakeRoom,
    string WakeSource,
    string? WakeFavoriteName,
    int WakeStartVolume,
    int WakeEndVolume,
    int WakeVolumeStep,
    int WakeStepIntervalMinutes,
    bool WakeExpandToHouse,
    bool WakeActive,
    bool GenaEnabled,
    bool ShowGenres,
    bool ShuffleExcludePlayed,
    bool ShuffleAutoTopUp,
    bool ContinueAfterSpecial,
    bool ShuffleArtistSpread,
    int ShuffleQueueTracks,
    int ShuffleTopUpTracks,
    int ShuffleHistoryDays,
    int ShuffleTopUpWhenRemaining,
    bool TagUpdateMaster,
    string ControlShuffleSource,
    string ControlShuffleLabel,
    string? PreferredCoordinator);

public sealed class RemoteSettingsBody
{
    public int? VolumeStep { get; set; }
    public int? LevelVolumePercent { get; set; }
    public bool? KeepHouseGrouped { get; set; }
    public bool? NightlyResetEnabled { get; set; }
    public int? NightlyResetMinutes { get; set; }
    public bool? NightlyResetReshuffle { get; set; }
    public bool? WakeEnabled { get; set; }
    public int? WakeMinutes { get; set; }
    public int? WakeDaysMask { get; set; }
    public string? WakeRoom { get; set; }
    public string? WakeSource { get; set; }
    public string? WakeFavoriteName { get; set; }
    public int? WakeStartVolume { get; set; }
    public int? WakeEndVolume { get; set; }
    public int? WakeVolumeStep { get; set; }
    public int? WakeStepIntervalMinutes { get; set; }
    public bool? WakeExpandToHouse { get; set; }
    public bool? GenaEnabled { get; set; }
    public bool? ShowGenres { get; set; }
    public bool? ShuffleExcludePlayed { get; set; }
    public bool? ShuffleAutoTopUp { get; set; }
    public bool? ContinueAfterSpecial { get; set; }
    public bool? ShuffleArtistSpread { get; set; }
    public int? ShuffleQueueTracks { get; set; }
    public int? ShuffleTopUpTracks { get; set; }
    public int? ShuffleHistoryDays { get; set; }
    public int? ShuffleTopUpWhenRemaining { get; set; }
    public bool? TagUpdateMaster { get; set; }
    public string? ControlShuffleSource { get; set; }
    public string? PreferredCoordinator { get; set; }
}

public sealed class RemoteFlagBody
{
    public bool Enabled { get; set; }
}

public sealed class RemoteRoomVolumeBody
{
    public string? Room { get; set; }
    public string? Ip { get; set; }
    public int? Percent { get; set; }
    public bool? Muted { get; set; }
}

public sealed class RemoteRebootBody
{
    public string? Ip { get; set; }
    public string? Room { get; set; }
    public bool All { get; set; }
}

public sealed class RemoteRescanBody
{
    public bool ForceAll { get; set; }
    public bool RediscoverRoots { get; set; }
}

public sealed class RemoteDailySelectBody
{
    public bool? All { get; set; }
    public string? Preset { get; set; }
    public bool PresetOn { get; set; } = true;
    public List<string>? Rooms { get; set; }
}

public sealed class RemoteDailySaveBody
{
    public string? Name { get; set; }
    public List<string>? Rooms { get; set; }
}

public sealed class RemoteTagCreateBody
{
    public string? Label { get; set; }
}

public sealed class RemoteTagRenameBody
{
    public string? Key { get; set; }
    public string? Label { get; set; }
}

public sealed class RemoteTagToggleBody
{
    public string? Path { get; set; }
    public string? Key { get; set; }
    public bool? ForceEnable { get; set; }
}

public sealed class RemoteTagSetBody
{
    public string? Path { get; set; }
    public List<string>? TagKeys { get; set; }
}

public sealed class RemoteSpotifyBody
{
    public string? Name { get; set; }
    public string? Reference { get; set; }
}

public sealed class RemoteSeekBody
{
    public int Seconds { get; set; }
}

public sealed record RemoteQueueResponse(bool Ok, IReadOnlyList<RemoteQueueItemDto> Tracks, string? Error);

public sealed record RemoteQueueItemDto(int Number, string Title, string Artist);

public sealed record RemoteLibraryRootDto(string Path, bool InDaily);

public sealed record RemoteLibraryConfigResponse(
    bool Ok,
    IReadOnlyList<RemoteLibraryRootDto> Roots,
    string? MasterRoot,
    int DistinctMasters,
    bool Scanning,
    int TrackCount,
    string? Phase,
    string? LastError,
    string? Note,
    string? Error);

public sealed class RemoteLibraryConfigBody
{
    public List<RemoteLibraryRootBody>? Roots { get; set; }
    public string? MasterRoot { get; set; }
}

public sealed class RemoteLibraryRootBody
{
    public string? Path { get; set; }
    public bool InDaily { get; set; }
}
