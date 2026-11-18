using System.Text.Json.Serialization;

namespace HotSonos.Remote.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RemoteStatusResponse))]
[JsonSerializable(typeof(RemoteCommandResponse))]
[JsonSerializable(typeof(RemoteVolumeSampleResponse))]
[JsonSerializable(typeof(RemoteSourcesResponse))]
[JsonSerializable(typeof(RemoteSearchResponse))]
[JsonSerializable(typeof(RemoteVolumeBody))]
[JsonSerializable(typeof(RemoteVolumeStepBody))]
[JsonSerializable(typeof(RemoteRoomBody))]
[JsonSerializable(typeof(RemoteKeyBody))]
[JsonSerializable(typeof(RemoteNameBody))]
[JsonSerializable(typeof(RemotePathBody))]
[JsonSerializable(typeof(RemoteTrackBody))]
[JsonSerializable(typeof(RemoteVolumesResponse))]
[JsonSerializable(typeof(RemoteTopologyResponse))]
[JsonSerializable(typeof(RemoteDailyGroupsResponse))]
[JsonSerializable(typeof(RemoteFavoritesResponse))]
[JsonSerializable(typeof(RemoteSpotifyListResponse))]
[JsonSerializable(typeof(RemoteLibraryStatusResponse))]
[JsonSerializable(typeof(RemoteTagsResponse))]
[JsonSerializable(typeof(RemoteSongsResponse))]
[JsonSerializable(typeof(RemotePlaysResponse))]
[JsonSerializable(typeof(RemoteMusicResponse))]
[JsonSerializable(typeof(RemoteEventsResponse))]
[JsonSerializable(typeof(RemoteHeardResponse))]
[JsonSerializable(typeof(RemoteLogsResponse))]
[JsonSerializable(typeof(RemoteSettingsResponse))]
[JsonSerializable(typeof(RemoteSettingsBody))]
[JsonSerializable(typeof(RemoteFlagBody))]
[JsonSerializable(typeof(RemoteRoomVolumeBody))]
[JsonSerializable(typeof(RemoteRebootBody))]
[JsonSerializable(typeof(RemoteRescanBody))]
[JsonSerializable(typeof(RemoteDailySelectBody))]
[JsonSerializable(typeof(RemoteDailySaveBody))]
[JsonSerializable(typeof(RemoteTagCreateBody))]
[JsonSerializable(typeof(RemoteTagRenameBody))]
[JsonSerializable(typeof(RemoteTagToggleBody))]
[JsonSerializable(typeof(RemoteTagSetBody))]
[JsonSerializable(typeof(RemoteSpotifyBody))]
public partial class RemoteJsonContext : JsonSerializerContext
{
}
