using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HotSonos.App.Infrastructure;
using HotSonos.App.Mcp;
using HotSonos.App.Models;
using HotSonos.Core.Models;
using HotSonos.Remote.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HotSonos.App.Remote;

/// <summary>Callbacks the remote host cannot see on <see cref="HotSonosMcpState"/>.</summary>
public sealed class RemoteHooks
{
    public required Func<bool> IsBusy { get; init; }
    public required Func<string> Version { get; init; }
    public required Func<int, CancellationToken, Task<int>> SetLogicalVolumeAsync { get; init; }
    public required Func<CancellationToken, Task<(int? Volume, bool? Muted)>> SampleVolumeAsync { get; init; }
}

/// <summary>
/// LAN HTTP API for the phone remote. Same process and same action gate as the tray.
/// MCP stays on loopback. Faults here are logged and answered; they do not stop the tray.
/// </summary>
public sealed partial class HotSonosRemoteHost : IAsyncDisposable
{
    private static readonly HttpClient ArtHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly object _artGate = new();
    private string? _artUri;
    private byte[]? _artBytes;
    private string _artType = "image/jpeg";

    private WebApplication? _app;
    private Task? _runTask;

    public string? Endpoint { get; private set; }
    public bool IsRunning => _app is not null && _runTask is { IsCompleted: false };

    /// <summary>Up, non-loopback, non-APIPA IPv4 addresses. Same filter as Sonos discovery.</summary>
    public static IReadOnlyList<string> LocalIpv4()
    {
        var addresses = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            if (!nic.SupportsMulticast)
                continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;
                addresses.Add(ua.Address.ToString());
            }
        }

        return addresses;
    }

    public async Task StartAsync(
        HotSonosMcpState state,
        RemoteHooks hooks,
        string ip,
        int port,
        CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);

        port = Math.Clamp(port, 1024, 65535);
        Endpoint = $"http://{ip}:{port}";

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = "HotSonos.Remote",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseUrls(Endpoint);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxConcurrentConnections = 20;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.AddServerHeader = false;
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.Configure<HostOptions>(o =>
        {
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            o.ShutdownTimeout = TimeSpan.FromSeconds(2);
        });

        var lifetime = new ManualHostLifetime();
        builder.Services.AddSingleton<IHostLifetime>(lifetime);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(hooks);

        _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            try
            {
                await next().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Remote request failed {ctx.Request.Method} {ctx.Request.Path}", ex);
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsJsonAsync(
                        new RemoteCommandResponse(false, null, "remote error"),
                        RemoteApi.Json).ConfigureAwait(false);
                }
            }
        });

        _app.Use(async (ctx, next) =>
        {
            var settings = state.Settings().EnsureShape();
            var provided = ctx.Request.Headers[RemoteApi.PinHeader].ToString();
            if (!RemoteNet.PinMatches(provided, settings.RemotePin))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(
                    new RemoteCommandResponse(false, null, "unauthorized"),
                    RemoteApi.Json).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });

        MapRoutes(_app, state, hooks);

        _runTask = _app.RunAsync();
        _ = _runTask.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                    AppLog.Error("Remote host run task faulted", t.Exception?.GetBaseException());
                else
                    AppLog.Info("Remote host run task completed (tray should keep running)");
            },
            TaskScheduler.Default);

        await Task.Delay(200, ct).ConfigureAwait(false);
        if (_runTask.IsFaulted)
        {
            var ex = _runTask.Exception?.GetBaseException()
                     ?? new InvalidOperationException("Remote host failed to start.");
            AppLog.Error($"Remote host failed on {Endpoint}", ex);
            try { await StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
            throw ex;
        }

        AppLog.Info($"Remote listening at {Endpoint}");
        AppLog.Lifecycle($"Remote listening {Endpoint}");
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;

        try
        {
            await _app.StopAsync().ConfigureAwait(false);
            if (_runTask is not null)
            {
                try { await _runTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AppLog.Warn("Remote host run ended with error", ex); }
            }

            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Remote host stop failed", ex);
        }
        finally
        {
            _app = null;
            _runTask = null;
            Endpoint = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void MapRoutes(WebApplication app, HotSonosMcpState state, RemoteHooks hooks)
    {
        app.MapGet("/api/status", () =>
        {
            var settings = state.Settings().EnsureShape();
            var np = state.GetLastNowPlaying();
            var artUri = np?.AlbumArtUri;
            return Results.Json(new RemoteStatusResponse(
                true,
                hooks.Version(),
                state.Sonos.ActiveRoom,
                state.Sonos.ActiveGroupLabel,
                state.Sonos.Groups.Select(g => new RemoteGroupDto(g.CoordinatorRoom, g.DisplayName, g.MemberCount)).ToList(),
                state.Sonos.OfflineSpeakers.ToList(),
                np is null
                    ? null
                    : new RemoteNowPlayingDto(
                        np.Title,
                        np.Artist,
                        np.Album,
                        np.State.ToString(),
                        np.SourceLabel,
                        np.CurrentTrack,
                        np.NumberOfTracks,
                        !string.IsNullOrWhiteSpace(artUri),
                        RemoteNet.ArtRevision(artUri)),
                state.Sonos.HouseLogicalVolume,
                state.Sonos.HouseMuted,
                state.Wake?.IsActive == true,
                hooks.IsBusy()), RemoteApi.Json);
        });

        app.MapGet("/api/art", async (HttpContext ctx) =>
        {
            var np = state.GetLastNowPlaying();
            var uri = np?.AlbumArtUri;
            if (!RemoteNet.IsAllowedArtUri(uri, state.Sonos.KnownSpeakerAddresses()))
                return Results.NotFound();

            var (bytes, type) = await GetArtAsync(uri!, ctx.RequestAborted).ConfigureAwait(false);
            if (bytes is null)
                return Results.NotFound();
            return Results.File(bytes, type);
        });

        app.MapPost("/api/volume/sample", async (HttpContext ctx) =>
        {
            Log(ctx);
            var (volume, muted) = await hooks.SampleVolumeAsync(ctx.RequestAborted).ConfigureAwait(false);
            return Results.Json(new RemoteVolumeSampleResponse(true, volume, muted, null), RemoteApi.Json);
        });

        app.MapPost("/api/transport/play-pause", Transport(state, HotsonosAction.PlayPause));
        app.MapPost("/api/transport/next", Transport(state, HotsonosAction.Next));
        app.MapPost("/api/transport/previous", Transport(state, HotsonosAction.Previous));
        app.MapPost("/api/transport/mute", Transport(state, HotsonosAction.Mute));
        app.MapPost("/api/volume/level", Transport(state, HotsonosAction.LevelVolumes));
        app.MapPost("/api/daily/play", Transport(state, HotsonosAction.ShuffleLibrary));
        app.MapPost("/api/wake/cancel", (HttpContext ctx) =>
        {
            Log(ctx);
            state.Wake?.Cancel();
            return Results.Json(new RemoteCommandResponse(true, "Wake cancel requested", null), RemoteApi.Json);
        });

        app.MapPost("/api/volume/step", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteVolumeStepBody>(ctx).ConfigureAwait(false);
            if (body is null || body.Direction is not (1 or -1))
                return BadJson();
            var action = body.Direction > 0 ? HotsonosAction.VolumeUp : HotsonosAction.VolumeDown;
            return await RunToast(state.ExecuteActionAsync(action)).ConfigureAwait(false);
        });

        app.MapPost("/api/volume", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteVolumeBody>(ctx).ConfigureAwait(false);
            if (body is null)
                return BadJson();
            var percent = Math.Clamp(body.Percent, 0, 100);
            var volume = await hooks.SetLogicalVolumeAsync(percent, ctx.RequestAborted).ConfigureAwait(false);
            return Results.Json(new RemoteCommandResponse(true, $"Volume {volume}%", null), RemoteApi.Json);
        });

        app.MapPost("/api/room", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteRoomBody>(ctx).ConfigureAwait(false);
            var room = body?.Room?.Trim();
            if (string.IsNullOrWhiteSpace(room))
                return BadJson();
            if (state.Sonos.TryGetGroup(room) is null)
            {
                return Results.Json(
                    new RemoteCommandResponse(false, null, "Room not found."),
                    RemoteApi.Json,
                    statusCode: StatusCodes.Status404NotFound);
            }

            state.SetActiveRoom?.Invoke(room);
            return Results.Json(new RemoteCommandResponse(true, state.Sonos.ActiveGroupLabel, null), RemoteApi.Json);
        });

        app.MapPost("/api/slots/{n:int}/play", async (HttpContext ctx, int n) =>
        {
            Log(ctx);
            if (n is < 1 or > 6)
                return BadJson();
            var action = HotsonosAction.Favorite1 + (n - 1);
            return await RunToast(state.ExecuteActionAsync(action)).ConfigureAwait(false);
        });

        app.MapPost("/api/tags/play", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteKeyBody>(ctx).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Key) || state.PlayTaggedTracksAsync is null)
                return BadJson();
            return await RunToast(async () => await state.PlayTaggedTracksAsync(body.Key.Trim(), true, ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapPost("/api/genres/play", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteNameBody>(ctx).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Name) || state.PlayGenreTracksAsync is null)
                return BadJson();
            return await RunToast(async () => await state.PlayGenreTracksAsync(body.Name.Trim(), true, ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapPost("/api/folders/play", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemotePathBody>(ctx).ConfigureAwait(false);
            var path = body?.Path?.Trim().TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(path) || state.PlayLibraryFolderAsync is null)
                return BadJson();
            var settings = state.Settings().EnsureShape();
            var known = settings.SonosLibraryRoots.Any(r =>
                string.Equals(r.Trim().TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
            if (!known)
                return Results.Json(new RemoteCommandResponse(false, null, "Folder is not a library root."), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            return await RunToast(async () => await state.PlayLibraryFolderAsync(path, true, ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapPost("/api/tracks/play", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteTrackBody>(ctx).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Path) || state.PlayLibraryTrackAsync is null)
                return BadJson();
            return await RunToast(async () => await state.PlayLibraryTrackAsync(body.Path.Trim(), body.Title, body.Artist, ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapPost("/api/tracks/queue", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteTrackBody>(ctx).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Path) || state.QueueNowAsync is null)
                return BadJson();
            return await RunToast(async () => await state.QueueNowAsync(body.Path.Trim(), body.Title, body.Artist, ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapGet("/api/sources", () =>
        {
            var settings = state.Settings().EnsureShape();
            var lib = state.Library;
            var slots = new List<RemoteSlotDto>(6);
            for (var i = 0; i < 6; i++)
            {
                var slot = i < settings.FavoriteSlots.Count ? settings.FavoriteSlots[i] : new FavoriteSlot();
                var kind = slot.IsTag ? "tag"
                    : slot.IsGenre ? "genre"
                    : slot.IsFolder ? "folder"
                    : slot.IsSonos ? "sonos"
                    : "empty";
                slots.Add(new RemoteSlotDto(i + 1, slot.DisplayLabel(settings), kind));
            }

            var tags = settings.Tags
                .Where(t => !string.IsNullOrWhiteSpace(t.Key))
                .Select(t => new RemoteTagDto(t.Key, string.IsNullOrWhiteSpace(t.Label) ? t.Key : t.Label))
                .ToList();

            var genres = settings.ShowGenresInPlaySources && lib is not null
                ? lib.ListGenres().Select(g => new RemoteGenreDto(g.Genre, g.Count)).ToList()
                : [];

            var daily = settings.GetEffectiveDailyLibraryRoots();
            var folders = settings.SonosLibraryRoots.Select(path =>
            {
                var trimmed = path.Trim().TrimEnd('\\', '/');
                var name = System.IO.Path.GetFileName(trimmed);
                return new RemoteFolderDto(
                    string.IsNullOrWhiteSpace(name) ? trimmed : name,
                    trimmed,
                    lib?.CountTracksUnderFolder(trimmed) ?? 0,
                    daily.Any(d => string.Equals(d.Trim().TrimEnd('\\', '/'), trimmed, StringComparison.OrdinalIgnoreCase)));
            }).ToList();

            var source = (settings.ControlShuffleSource ?? "all").Trim();
            return Results.Json(new RemoteSourcesResponse(
                true,
                new RemoteDailyDto("Daily"),
                slots,
                tags,
                genres,
                folders,
                source,
                DescribeShuffleSource(settings, source)), RemoteApi.Json);
        });

        MapMoreRoutes(app, state, hooks);

        app.MapGet("/api/library/search", (HttpContext ctx) =>
        {
            Log(ctx);
            var lib = state.Library;
            var limit = 25;
            if (int.TryParse(ctx.Request.Query["limit"], out var parsedLimit))
                limit = Math.Clamp(parsedLimit, 1, 50);
            var offset = 0;
            if (int.TryParse(ctx.Request.Query["offset"], out var parsedOffset) && parsedOffset > 0)
                offset = parsedOffset;
            var query = ctx.Request.Query["q"].ToString();
            if (lib is null)
                return Results.Json(new RemoteSearchResponse(true, 0, []), RemoteApi.Json);

            var settings = state.Settings().EnsureShape();
            var rows = lib.Search(string.IsNullOrWhiteSpace(query) ? null : query, limit, offset);
            var tracks = rows.Select(t => new RemoteTrackDto(
                t.Path,
                t.Title,
                t.Artist,
                t.Album,
                t.Genre,
                t.FormatTagLabels(settings.TagLabel))).ToList();
            return Results.Json(new RemoteSearchResponse(true, tracks.Count, tracks), RemoteApi.Json);
        });
    }

    private async Task<(byte[]? Bytes, string Type)> GetArtAsync(string uri, CancellationToken ct)
    {
        lock (_artGate)
        {
            if (_artBytes is not null && string.Equals(_artUri, uri, StringComparison.Ordinal))
                return (_artBytes, _artType);
        }

        try
        {
            using var response = await ArtHttp.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, "image/jpeg");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0)
                return (null, "image/jpeg");
            var type = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(type) || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                type = "image/jpeg";
            lock (_artGate)
            {
                _artUri = uri;
                _artBytes = bytes;
                _artType = type;
            }

            return (bytes, type);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Remote album art load failed", ex);
            return (null, "image/jpeg");
        }
    }

    private static void Log(HttpContext ctx) =>
        AppLog.Info($"Remote {ctx.Request.Method} {ctx.Request.Path}");

    private static async Task<T?> ReadJson<T>(HttpContext ctx) where T : class
    {
        try
        {
            return await ctx.Request.ReadFromJsonAsync<T>(RemoteApi.Json, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static IResult BadJson() =>
        Results.Json(new RemoteCommandResponse(false, null, "Bad request."), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Route handler as <see cref="Delegate"/> so the <see cref="IResult"/> is written (ASP0016).</summary>
    private static Delegate Transport(HotSonosMcpState state, HotsonosAction action) =>
        async (HttpContext ctx) => await Command(ctx, () => state.ExecuteActionAsync(action)).ConfigureAwait(false);

    private static Task<IResult> Command(HttpContext ctx, Func<Task<string?>> run)
    {
        Log(ctx);
        return RunToast(run);
    }

    private static async Task<IResult> RunToast(Func<Task<string?>> run)
    {
        try
        {
            return Toast(await run().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Remote command failed: {ex.Message}");
            return Results.Json(new RemoteCommandResponse(false, null, ex.Message), RemoteApi.Json);
        }
    }

    private static async Task<IResult> RunToast(Task<string?> run)
    {
        try
        {
            return Toast(await run.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Remote command failed: {ex.Message}");
            return Results.Json(new RemoteCommandResponse(false, null, ex.Message), RemoteApi.Json);
        }
    }

    private static IResult Toast(string? toast)
    {
        if (!string.IsNullOrWhiteSpace(toast)
            && toast.StartsWith("Sonos error:", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new RemoteCommandResponse(false, null, toast), RemoteApi.Json);

        return Results.Json(new RemoteCommandResponse(true, toast, null), RemoteApi.Json);
    }
}
