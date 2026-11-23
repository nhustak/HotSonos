using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

public enum RemoteCallKind
{
    Ok,
    Unauthorized,
    Unreachable,
    Timeout,
    Failed,
    Cancelled,
}

public sealed record RemoteCall<T>(T? Value, RemoteCallKind Kind, string? Detail);

/// <summary>
/// Talks only to the tray app. Redirects are off so the house PIN is not forwarded.
/// Status, art, search, and sources use 8 seconds. Play, shuffle, and queue use 90.
/// </summary>
public sealed class RemoteClient : IDisposable
{
    static readonly TimeSpan Fast = TimeSpan.FromSeconds(8);
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(90);

    readonly HttpClient _http;
    PcTarget? _target;

    public RemoteClient()
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
    {
    }

    /// <summary>Test hook. The handler receives each request after the address and PIN are stamped on it.</summary>
    public RemoteClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Point later requests at this PC. Safe to call again after requests have
    /// already been sent: the address and PIN are stamped onto each request,
    /// because HttpClient freezes BaseAddress and default headers after the first send.
    /// </summary>
    public void Apply(string host, int port, string pin)
    {
        _target = new PcTarget(new UriBuilder("http", host, port).Uri, pin ?? "");
    }

    sealed record PcTarget(Uri BaseUri, string Pin);

    public void Dispose() => _http.Dispose();

    public Task<RemoteCall<RemoteStatusResponse>> GetStatusAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/status", null, Fast, RemoteJsonContext.Default.RemoteStatusResponse, ct);

    /// <summary>
    /// Unauthenticated <c>POST /api/pair</c>. Does not keep the password, and does not
    /// change the saved PIN until the caller stores the returned host, port, and PIN.
    /// </summary>
    public async Task<RemoteCall<RemotePairResponse>> PairAsync(string host, int port, string password, CancellationToken ct)
    {
        try
        {
            if (!RemoteNet.TryParseAddress(host, port.ToString(System.Globalization.CultureInfo.InvariantCulture), out var parsedHost, out var parsedPort, out var error))
                return new(null, RemoteCallKind.Failed, error ?? "Enter the PC address.");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Fast);
            var uri = new UriBuilder("http", parsedHost, parsedPort) { Path = "/api/pair" }.Uri;
            var json = JsonSerializer.Serialize(new RemotePairRequest(password), RemoteJsonContext.Default.RemotePairRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            RemotePairResponse? value = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    value = JsonSerializer.Deserialize(text, RemoteJsonContext.Default.RemotePairResponse);
                }
                catch (JsonException)
                {
                    return new(null, RemoteCallKind.Failed, "Bad response");
                }
            }

            if (response.IsSuccessStatusCode && value is { Ok: true }
                && !string.IsNullOrWhiteSpace(value.Host)
                && RemoteNet.IsSixDigitPin(value.Pin))
                return new(value, RemoteCallKind.Ok, null);

            var detail = value?.Error;
            if (string.IsNullOrWhiteSpace(detail))
                detail = "Could not pair.";
            return new(value, RemoteCallKind.Failed, detail);
        }
        catch (Exception ex) when (Classify(ex, ct) is { } failure)
        {
            return new(null, failure.Kind, failure.Detail);
        }
    }

    public Task<RemoteCall<RemoteSourcesResponse>> GetSourcesAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/sources", null, Fast, RemoteJsonContext.Default.RemoteSourcesResponse, ct);

    public Task<RemoteCall<RemoteSearchResponse>> SearchAsync(string query, int limit, int offset, CancellationToken ct)
    {
        var path = "api/library/search?q=" + Uri.EscapeDataString(query)
            + "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return SendAsync(HttpMethod.Get, path, null, Fast, RemoteJsonContext.Default.RemoteSearchResponse, ct);
    }

    public Task<RemoteCall<RemoteVolumeSampleResponse>> SampleVolumeAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "api/volume/sample", null, Fast, RemoteJsonContext.Default.RemoteVolumeSampleResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SetVolumeAsync(int percent, CancellationToken ct) =>
        PostAsync("api/volume", new RemoteVolumeBody { Percent = percent }, RemoteJsonContext.Default.RemoteVolumeBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> StepVolumeAsync(int direction, CancellationToken ct) =>
        PostAsync("api/volume/step", new RemoteVolumeStepBody { Direction = direction }, RemoteJsonContext.Default.RemoteVolumeStepBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SeekAsync(int seconds, CancellationToken ct) =>
        PostAsync("api/transport/seek", new RemoteSeekBody { Seconds = seconds }, RemoteJsonContext.Default.RemoteSeekBody, Fast, ct);

    public Task<RemoteCall<RemoteQueueResponse>> GetQueueAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/queue", null, Fast, RemoteJsonContext.Default.RemoteQueueResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PlayPauseAsync(CancellationToken ct) =>
        EmptyAsync("api/transport/play-pause", Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> NextAsync(CancellationToken ct) =>
        EmptyAsync("api/transport/next", Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PreviousAsync(CancellationToken ct) =>
        EmptyAsync("api/transport/previous", Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> MuteAsync(CancellationToken ct) =>
        EmptyAsync("api/transport/mute", Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> LevelAsync(CancellationToken ct) =>
        EmptyAsync("api/volume/level", Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> CancelWakeAsync(CancellationToken ct) =>
        EmptyAsync("api/wake/cancel", Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SetRoomAsync(string room, CancellationToken ct) =>
        PostAsync("api/room", new RemoteRoomBody { Room = room }, RemoteJsonContext.Default.RemoteRoomBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PlayDailyAsync(CancellationToken ct) =>
        EmptyAsync("api/daily/play", Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PlaySlotAsync(int index, CancellationToken ct) =>
        EmptyAsync("api/slots/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/play", Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PlayTagAsync(string key, CancellationToken ct) =>
        PostAsync("api/tags/play", new RemoteKeyBody { Key = key }, RemoteJsonContext.Default.RemoteKeyBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PlayGenreAsync(string name, CancellationToken ct) =>
        PostAsync("api/genres/play", new RemoteNameBody { Name = name }, RemoteJsonContext.Default.RemoteNameBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PlayFolderAsync(string path, CancellationToken ct) =>
        PostAsync("api/folders/play", new RemotePathBody { Path = path }, RemoteJsonContext.Default.RemotePathBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PlayTrackAsync(string path, string? title, string? artist, CancellationToken ct) =>
        PostAsync("api/tracks/play", new RemoteTrackBody { Path = path, Title = title, Artist = artist }, RemoteJsonContext.Default.RemoteTrackBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> QueueTrackAsync(string path, string? title, string? artist, CancellationToken ct) =>
        PostAsync("api/tracks/queue", new RemoteTrackBody { Path = path, Title = title, Artist = artist }, RemoteJsonContext.Default.RemoteTrackBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> AppendTrackAsync(string path, string? title, string? artist, CancellationToken ct) =>
        PostAsync("api/tracks/queue/append", new RemoteTrackBody { Path = path, Title = title, Artist = artist }, RemoteJsonContext.Default.RemoteTrackBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> ReplaceTrackAsync(string path, string? title, string? artist, CancellationToken ct) =>
        PostAsync("api/tracks/replace", new RemoteTrackBody { Path = path, Title = title, Artist = artist }, RemoteJsonContext.Default.RemoteTrackBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> FreshStartAsync(CancellationToken ct) =>
        EmptyAsync("api/transport/fresh-start", Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> ResumeShuffleAsync(CancellationToken ct) =>
        EmptyAsync("api/shuffle/resume", Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> StartShuffleAsync(CancellationToken ct) =>
        EmptyAsync("api/shuffle/start", Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SetShuffleSourceAsync(string token, CancellationToken ct) =>
        PostAsync("api/shuffle/source", new RemoteNameBody { Name = token }, RemoteJsonContext.Default.RemoteNameBody, Fast, ct);

    public Task<RemoteCall<RemoteVolumesResponse>> GetVolumesAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/volumes", null, Fast, RemoteJsonContext.Default.RemoteVolumesResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SetRoomVolumeAsync(string? room, string? ip, int? percent, bool? muted, CancellationToken ct) =>
        PostAsync("api/volumes/room", new RemoteRoomVolumeBody { Room = room, Ip = ip, Percent = percent, Muted = muted }, RemoteJsonContext.Default.RemoteRoomVolumeBody, Fast, ct);

    public Task<RemoteCall<RemoteFavoritesResponse>> GetFavoritesAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/favorites", null, Fast, RemoteJsonContext.Default.RemoteFavoritesResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PlayFavoriteAsync(string name, CancellationToken ct) =>
        PostAsync("api/favorites/play", new RemoteNameBody { Name = name }, RemoteJsonContext.Default.RemoteNameBody, Slow, ct);

    public Task<RemoteCall<RemoteSpotifyListResponse>> GetSpotifyAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/spotify", null, Fast, RemoteJsonContext.Default.RemoteSpotifyListResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> PlaySpotifyAsync(string reference, CancellationToken ct) =>
        PostAsync("api/spotify/play", new RemoteSpotifyBody { Reference = reference }, RemoteJsonContext.Default.RemoteSpotifyBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SaveSpotifyAsync(string? name, string reference, CancellationToken ct) =>
        PostAsync("api/spotify/save", new RemoteSpotifyBody { Name = name, Reference = reference }, RemoteJsonContext.Default.RemoteSpotifyBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> RemoveSpotifyAsync(string reference, CancellationToken ct) =>
        PostAsync("api/spotify/remove", new RemoteSpotifyBody { Reference = reference }, RemoteJsonContext.Default.RemoteSpotifyBody, Fast, ct);

    public Task<RemoteCall<RemoteTopologyResponse>> GetTopologyAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/topology", null, Fast, RemoteJsonContext.Default.RemoteTopologyResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> RefreshTopologyAsync(CancellationToken ct) =>
        EmptyAsync("api/topology/refresh", Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> RegroupAsync(string? room, CancellationToken ct) =>
        PostAsync("api/topology/regroup", new RemoteRoomBody { Room = room }, RemoteJsonContext.Default.RemoteRoomBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> RebootAsync(string? room, string? ip, bool all, CancellationToken ct) =>
        PostAsync("api/topology/reboot", new RemoteRebootBody { Room = room, Ip = ip, All = all }, RemoteJsonContext.Default.RemoteRebootBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> KeepGroupedAsync(bool enabled, CancellationToken ct) =>
        PostAsync("api/house/keep-grouped", new RemoteFlagBody { Enabled = enabled }, RemoteJsonContext.Default.RemoteFlagBody, Fast, ct);

    public Task<RemoteCall<RemoteDailyGroupsResponse>> GetDailyGroupsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/daily-groups", null, Fast, RemoteJsonContext.Default.RemoteDailyGroupsResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SelectDailyAsync(RemoteDailySelectBody body, CancellationToken ct) =>
        PostAsync("api/daily-groups/select", body, RemoteJsonContext.Default.RemoteDailySelectBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SaveDailyAsync(string name, IReadOnlyList<string> rooms, CancellationToken ct) =>
        PostAsync("api/daily-groups", new RemoteDailySaveBody { Name = name, Rooms = rooms.ToList() }, RemoteJsonContext.Default.RemoteDailySaveBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> RemoveDailyAsync(string name, CancellationToken ct) =>
        PostAsync("api/daily-groups/remove", new RemoteNameBody { Name = name }, RemoteJsonContext.Default.RemoteNameBody, Fast, ct);

    public Task<RemoteCall<RemoteLayoutsResponse>> GetLayoutsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/layouts", null, Fast, RemoteJsonContext.Default.RemoteLayoutsResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> ApplyLayoutAsync(string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/layouts/{Uri.EscapeDataString(id)}/apply", null, Slow, RemoteJsonContext.Default.RemoteCommandResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SaveLayoutAsync(RemoteLayoutSaveBody body, CancellationToken ct) =>
        PostAsync("api/layouts", body, RemoteJsonContext.Default.RemoteLayoutSaveBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> DeleteLayoutAsync(string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/layouts/{Uri.EscapeDataString(id)}/delete", null, Fast, RemoteJsonContext.Default.RemoteCommandResponse, ct);

    public Task<RemoteCall<RemoteLibraryStatusResponse>> GetLibraryStatusAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/library/status", null, Fast, RemoteJsonContext.Default.RemoteLibraryStatusResponse, ct);

    public Task<RemoteCall<RemoteLibraryConfigResponse>> GetLibraryConfigAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/library/config", null, Fast, RemoteJsonContext.Default.RemoteLibraryConfigResponse, ct);

    public Task<RemoteCall<RemoteLibraryConfigResponse>> SaveLibraryConfigAsync(RemoteLibraryConfigBody body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "api/library/config", Json(body, RemoteJsonContext.Default.RemoteLibraryConfigBody), Fast, RemoteJsonContext.Default.RemoteLibraryConfigResponse, ct);

    public Task<RemoteCall<RemoteLibraryConfigResponse>> DiscoverLibraryAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "api/library/discover", null, Slow, RemoteJsonContext.Default.RemoteLibraryConfigResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> RescanAsync(bool forceAll, CancellationToken ct) =>
        PostAsync("api/library/rescan", new RemoteRescanBody { ForceAll = forceAll }, RemoteJsonContext.Default.RemoteRescanBody, Fast, ct);

    public Task<RemoteCall<RemoteTagsResponse>> GetTagsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/tags", null, Fast, RemoteJsonContext.Default.RemoteTagsResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> CreateTagAsync(string label, CancellationToken ct) =>
        PostAsync("api/tags", new RemoteTagCreateBody { Label = label }, RemoteJsonContext.Default.RemoteTagCreateBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> RenameTagAsync(string key, string label, CancellationToken ct) =>
        PostAsync("api/tags/rename", new RemoteTagRenameBody { Key = key, Label = label }, RemoteJsonContext.Default.RemoteTagRenameBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> DeleteTagAsync(string key, CancellationToken ct) =>
        PostAsync("api/tags/delete", new RemoteKeyBody { Key = key }, RemoteJsonContext.Default.RemoteKeyBody, Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> ToggleTagAsync(string path, string key, CancellationToken ct) =>
        PostAsync("api/tracks/tags", new RemoteTagToggleBody { Path = path, Key = key }, RemoteJsonContext.Default.RemoteTagToggleBody, Slow, ct);

    public Task<RemoteCall<RemoteNowTagsResponse>> GetNowTagsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/now/tags", null, Fast, RemoteJsonContext.Default.RemoteNowTagsResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> ToggleNowTagAsync(string key, CancellationToken ct) =>
        PostAsync("api/now/tags", new RemoteKeyBody { Key = key }, RemoteJsonContext.Default.RemoteKeyBody, Slow, ct);

    public Task<RemoteCall<RemoteSongsResponse>> GetSongsAsync(string? query, int limit, int offset, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/reports/songs?q=" + Uri.EscapeDataString(query ?? "") + "&limit=" + limit + "&offset=" + offset, null, Fast, RemoteJsonContext.Default.RemoteSongsResponse, ct);

    public Task<RemoteCall<RemotePlaysResponse>> GetPlaysAsync(string trackKey, int limit, int offset, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/reports/plays?trackKey=" + Uri.EscapeDataString(trackKey) + "&limit=" + limit + "&offset=" + offset, null, Fast, RemoteJsonContext.Default.RemotePlaysResponse, ct);

    public Task<RemoteCall<RemoteMusicResponse>> GetMusicAsync(string? from, string? to, bool skippedOnly, int limit, int offset, CancellationToken ct)
    {
        var path = "api/reports/music?limit=" + limit + "&offset=" + offset + "&skippedOnly=" + (skippedOnly ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(from))
            path += "&from=" + Uri.EscapeDataString(from);
        if (!string.IsNullOrWhiteSpace(to))
            path += "&to=" + Uri.EscapeDataString(to);
        return SendAsync(HttpMethod.Get, path, null, Fast, RemoteJsonContext.Default.RemoteMusicResponse, ct);
    }

    public Task<RemoteCall<RemoteEventsResponse>> GetEventsAsync(string? category, string? kind, int limit, int offset, CancellationToken ct)
    {
        var path = "api/reports/events?limit=" + limit + "&offset=" + offset;
        if (!string.IsNullOrWhiteSpace(category))
            path += "&category=" + Uri.EscapeDataString(category);
        if (!string.IsNullOrWhiteSpace(kind))
            path += "&kind=" + Uri.EscapeDataString(kind);
        return SendAsync(HttpMethod.Get, path, null, Fast, RemoteJsonContext.Default.RemoteEventsResponse, ct);
    }

    public Task<RemoteCall<RemoteHeardResponse>> GetHeardAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/heard", null, Fast, RemoteJsonContext.Default.RemoteHeardResponse, ct);

    public Task<RemoteCall<RemoteLogsResponse>> GetLogsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/logs?max=60", null, Fast, RemoteJsonContext.Default.RemoteLogsResponse, ct);

    public Task<RemoteCall<RemoteSettingsResponse>> GetSettingsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "api/settings", null, Fast, RemoteJsonContext.Default.RemoteSettingsResponse, ct);

    public Task<RemoteCall<RemoteCommandResponse>> SaveSettingsAsync(RemoteSettingsBody body, CancellationToken ct) =>
        PostAsync("api/settings", body, RemoteJsonContext.Default.RemoteSettingsBody, Fast, ct);

    public Task<RemoteCall<RemoteCommandResponse>> WakeNowAsync(CancellationToken ct) =>
        EmptyAsync("api/wake/now", Slow, ct);

    public Task<RemoteCall<RemoteCommandResponse>> FailureDiagnosticAsync(CancellationToken ct) =>
        EmptyAsync("api/diagnostics/failure", Slow, ct);

    public async Task<RemoteCall<byte[]>> GetArtAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Fast);
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/art");
            Stamp(request);
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(null, RemoteCallKind.Unauthorized, RemoteFailureText.Describe(RemoteFailureKind.WrongPin, null));
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(null, RemoteCallKind.Ok, null);
            if (!response.IsSuccessStatusCode)
                return new(null, RemoteCallKind.Failed, "Art unavailable");
            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            return new(bytes, RemoteCallKind.Ok, null);
        }
        catch (Exception ex) when (Classify(ex, ct) is { } failure)
        {
            return new(null, failure.Kind, failure.Detail);
        }
    }

    Task<RemoteCall<RemoteCommandResponse>> EmptyAsync(string path, TimeSpan timeout, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, null, timeout, RemoteJsonContext.Default.RemoteCommandResponse, ct);

    Task<RemoteCall<RemoteCommandResponse>> PostAsync<T>(string path, T body, JsonTypeInfo<T> info, TimeSpan timeout, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, info);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return SendAsync(HttpMethod.Post, path, content, timeout, RemoteJsonContext.Default.RemoteCommandResponse, ct);
    }

    async Task<RemoteCall<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        HttpContent? content,
        TimeSpan timeout,
        JsonTypeInfo<T> info,
        CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var request = new HttpRequestMessage(method, path) { Content = content };
            Stamp(request);
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(default, RemoteCallKind.Unauthorized, RemoteFailureText.Describe(RemoteFailureKind.WrongPin, null));

            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            T? value = default;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    value = JsonSerializer.Deserialize(text, info);
                }
                catch (JsonException)
                {
                    return new(default, RemoteCallKind.Failed, "Bad response");
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = (value as RemoteCommandResponse)?.Error ?? "Request failed";
                return new(value, RemoteCallKind.Failed, detail);
            }

            if (value is null)
                return new(default, RemoteCallKind.Failed, "Bad response");

            return new(value, RemoteCallKind.Ok, null);
        }
        catch (Exception ex) when (Classify(ex, ct) is { } failure)
        {
            return new(default, failure.Kind, failure.Detail);
        }
    }

    void Stamp(HttpRequestMessage request)
    {
        var target = _target ?? throw new InvalidOperationException("Apply the PC address before sending.");
        var path = request.RequestUri;
        request.RequestUri = path is { IsAbsoluteUri: true }
            ? path
            : new Uri(target.BaseUri, path?.OriginalString ?? "");
        request.Headers.Remove(RemoteApi.PinHeader);
        request.Headers.TryAddWithoutValidation(RemoteApi.PinHeader, target.Pin);
    }

    static StringContent Json<T>(T body, JsonTypeInfo<T> info)
    {
        var json = JsonSerializer.Serialize(body, info);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    static (RemoteCallKind Kind, string Detail)? Classify(Exception ex, CancellationToken ct)
    {
        if (ex is ObjectDisposedException)
            return (RemoteCallKind.Cancelled, RemoteFailureText.Describe(RemoteFailureKind.Cancelled, null));
        if (ex is OperationCanceledException)
        {
            var kind = RemoteFailureText.Classify(ct.IsCancellationRequested, TransportFault.Canceled);
            var call = kind == RemoteFailureKind.Cancelled ? RemoteCallKind.Cancelled : RemoteCallKind.Timeout;
            return (call, RemoteFailureText.Describe(kind, null));
        }

        if (ex is HttpRequestException)
            return (RemoteCallKind.Unreachable, RemoteFailureText.Describe(RemoteFailureKind.Unreachable, ex.Message));
        if (ex is SocketException)
            return (RemoteCallKind.Unreachable, RemoteFailureText.Describe(RemoteFailureKind.Unreachable, ex.Message));
        if (ex is IOException)
            return (RemoteCallKind.Unreachable, RemoteFailureText.Describe(RemoteFailureKind.Unreachable, ex.Message));
        return null;
    }
}
