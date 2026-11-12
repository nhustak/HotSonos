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
public partial class RemoteJsonContext : JsonSerializerContext
{
}
