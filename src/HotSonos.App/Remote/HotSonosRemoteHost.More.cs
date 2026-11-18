using HotSonos.App.Infrastructure;
using HotSonos.App.Library;
using HotSonos.App.Mcp;
using HotSonos.App.Models;
using HotSonos.Core;
using HotSonos.Core.Models;
using HotSonos.Remote.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HotSonos.App.Remote;

public sealed partial class HotSonosRemoteHost
{
    /// <summary>
    /// Full-control routes. Each one calls the same Sonos manager, library service,
    /// or <see cref="HotSonosMcpState.ExecuteActionAsync"/> gate the tray and MCP use.
    /// </summary>
    private void MapMoreRoutes(WebApplication app, HotSonosMcpState state, RemoteHooks hooks)
    {
        app.MapPost("/api/transport/fresh-start", Transport(state, HotsonosAction.FreshStart));
        app.MapPost("/api/diagnostics/failure", Transport(state, HotsonosAction.FailureDiagnostic));

        app.MapPost("/api/shuffle/resume", async (HttpContext ctx) =>
        {
            Log(ctx);
            if (state.ResumeShuffleAsync is null)
                return BadJson();
            return await RunToast(async () => await state.ResumeShuffleAsync(ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapPost("/api/shuffle/start", async (HttpContext ctx) =>
        {
            Log(ctx);
            return await RunToast(() => StartControlShuffleAsync(state, ctx.RequestAborted)).ConfigureAwait(false);
        });

        app.MapPost("/api/shuffle/source", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteNameBody>(ctx).ConfigureAwait(false);
            var token = body?.Name?.Trim();
            if (!RemoteSettingsRules.IsShuffleSource(token))
                return BadJson();
            var settings = state.Settings().EnsureShape();
            if (token!.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                var path = token["folder:".Length..].Trim().TrimEnd('\\', '/');
                var known = settings.SonosLibraryRoots.Any(r =>
                    string.Equals(r.Trim().TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
                if (!known)
                    return Results.Json(new RemoteCommandResponse(false, null, "Folder is not a library root."), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            }

            settings.ControlShuffleSource = token;
            SaveSettings(state);
            return Results.Json(new RemoteCommandResponse(true, DescribeShuffleSource(settings, token), null), RemoteApi.Json);
        });

        app.MapPost("/api/tracks/replace", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteTrackBody>(ctx).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Path) || state.ReplaceQueueWithTrackAsync is null)
                return BadJson();
            return await RunToast(async () => await state.ReplaceQueueWithTrackAsync(body.Path.Trim(), body.Title, body.Artist, ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapGet("/api/volumes", async (HttpContext ctx) =>
        {
            Log(ctx);
            try
            {
                var rows = await state.Sonos.GetSpeakerVolumesAsync(ctx.RequestAborted).ConfigureAwait(false);
                var speakers = rows.Select(s => new RemoteSpeakerVolumeDto(s.RoomName, s.IpAddress, s.Volume, s.Muted, s.Reachable)).ToList();
                return Results.Json(new RemoteVolumesResponse(true, state.Sonos.HouseLogicalVolume, state.Sonos.HouseMuted, speakers, null), RemoteApi.Json);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Remote volumes failed", ex);
                return Results.Json(new RemoteVolumesResponse(false, state.Sonos.HouseLogicalVolume, state.Sonos.HouseMuted, [], ex.Message), RemoteApi.Json);
            }
        });

        app.MapPost("/api/volumes/room", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteRoomVolumeBody>(ctx).ConfigureAwait(false);
            if (body is null || (body.Percent is null && body.Muted is null))
                return BadJson();
            var zone = FindZone(state, body.Room, body.Ip);
            if (zone is null)
                return Results.Json(new RemoteCommandResponse(false, null, "Room not found."), RemoteApi.Json, statusCode: StatusCodes.Status404NotFound);
            try
            {
                if (body.Percent is int percent)
                    await state.Sonos.SetSpeakerVolumeAsync(zone.Value.Ip, Math.Clamp(percent, 0, 100), ctx.RequestAborted).ConfigureAwait(false);
                if (body.Muted is bool muted)
                    await state.Sonos.SetSpeakerMuteAsync(zone.Value.Ip, muted, ctx.RequestAborted).ConfigureAwait(false);
                return Results.Json(new RemoteCommandResponse(true, zone.Value.Room, null), RemoteApi.Json);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Remote room volume failed: {ex.Message}");
                return Results.Json(new RemoteCommandResponse(false, null, ex.Message), RemoteApi.Json);
            }
        });

        app.MapGet("/api/favorites", async (HttpContext ctx) =>
        {
            Log(ctx);
            try
            {
                var rows = await state.Sonos.GetFavoritesAsync(ctx.RequestAborted).ConfigureAwait(false);
                var list = rows.Select(f => new RemoteFavoriteDto(f.Title, f.Kind.ToString())).ToList();
                return Results.Json(new RemoteFavoritesResponse(true, list, null), RemoteApi.Json);
            }
            catch (Exception ex)
            {
                return Results.Json(new RemoteFavoritesResponse(false, [], ex.Message), RemoteApi.Json);
            }
        });

        app.MapPost("/api/favorites/play", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteNameBody>(ctx).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Name))
                return BadJson();
            return await RunToast(async () => await state.Sonos.PlaySonosFavoriteByNameAsync(body.Name.Trim(), ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapGet("/api/spotify", () =>
        {
            var settings = state.Settings().EnsureShape();
            var links = settings.SavedSpotifyLinks
                .Select(s => new RemoteSpotifyDto(s.Name, s.Reference))
                .ToList();
            return Results.Json(new RemoteSpotifyListResponse(true, state.Sonos.SpotifyIsLinked, links, null), RemoteApi.Json);
        });

        app.MapPost("/api/spotify/play", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteSpotifyBody>(ctx).ConfigureAwait(false);
            var reference = body?.Reference?.Trim();
            if (string.IsNullOrWhiteSpace(reference))
                reference = body?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(reference))
                return BadJson();
            return await RunToast(async () => await state.Sonos.PlaySpotifyAsync(reference, ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapPost("/api/spotify/save", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteSpotifyBody>(ctx).ConfigureAwait(false);
            if (!SpotifyItem.TryParse(body?.Reference, out var item) || item is null)
                return Results.Json(new RemoteCommandResponse(false, null, "Paste a Spotify link."), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            var settings = state.Settings().EnsureShape();
            var name = string.IsNullOrWhiteSpace(body!.Name) ? SavedSpotifyLink.DefaultName(item) : body.Name.Trim();
            settings.SavedSpotifyLinks.RemoveAll(s =>
                string.Equals(s.Reference, item.Canonical, StringComparison.OrdinalIgnoreCase));
            settings.SavedSpotifyLinks.Add(new SavedSpotifyLink { Name = name, Reference = item.Canonical });
            SaveSettings(state);
            return Results.Json(new RemoteCommandResponse(true, name, null), RemoteApi.Json);
        });

        app.MapPost("/api/spotify/remove", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteSpotifyBody>(ctx).ConfigureAwait(false);
            var reference = body?.Reference?.Trim();
            if (string.IsNullOrWhiteSpace(reference))
                return BadJson();
            var settings = state.Settings().EnsureShape();
            var removed = settings.SavedSpotifyLinks.RemoveAll(s =>
                string.Equals(s.Reference, reference, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Name, reference, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return Results.Json(new RemoteCommandResponse(false, null, "Saved link not found."), RemoteApi.Json, statusCode: StatusCodes.Status404NotFound);
            SaveSettings(state);
            return Results.Json(new RemoteCommandResponse(true, "Removed", null), RemoteApi.Json);
        });

        app.MapGet("/api/topology", () => Results.Json(BuildTopology(state), RemoteApi.Json));

        app.MapPost("/api/topology/refresh", async (HttpContext ctx) =>
        {
            Log(ctx);
            return await RunToast(async () => await state.RefreshDevicesAsync().ConfigureAwait(false)).ConfigureAwait(false);
        });

        app.MapPost("/api/topology/regroup", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteRoomBody>(ctx).ConfigureAwait(false);
            var settings = state.Settings().EnsureShape();
            var room = body?.Room?.Trim();
            if (string.IsNullOrWhiteSpace(room))
                room = settings.PreferredHouseCoordinatorRoom ?? state.Sonos.ActiveRoom ?? settings.ActiveRoom;
            if (string.IsNullOrWhiteSpace(room))
                return Results.Json(new RemoteCommandResponse(false, null, "No preferred or active room."), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var msg = await state.Sonos.SetHouseCoordinatorAsync(room, ctx.RequestAborted).ConfigureAwait(false);
                settings.PreferredHouseCoordinatorRoom = room;
                settings.ActiveRoom = room;
                state.SetActiveRoom?.Invoke(room);
                SaveSettings(state);
                return Results.Json(new RemoteCommandResponse(true, msg, null), RemoteApi.Json);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Remote regroup failed", ex);
                return Results.Json(new RemoteCommandResponse(false, null, ex.Message), RemoteApi.Json);
            }
        });

        app.MapPost("/api/topology/reboot", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteRebootBody>(ctx).ConfigureAwait(false);
            if (body is null)
                return BadJson();
            try
            {
                if (body.All)
                    return Results.Json(new RemoteCommandResponse(true, await RebootAllAsync(state, ctx.RequestAborted).ConfigureAwait(false), null), RemoteApi.Json);

                var zone = FindZone(state, body.Room, body.Ip);
                if (zone is null)
                    return Results.Json(new RemoteCommandResponse(false, null, "Room not found."), RemoteApi.Json, statusCode: StatusCodes.Status404NotFound);
                var status = await state.Sonos.RebootPlayerAsync(zone.Value.Ip, ctx.RequestAborted).ConfigureAwait(false);
                return Results.Json(new RemoteCommandResponse(true, $"{zone.Value.Room}: {status}", null), RemoteApi.Json);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Remote reboot failed", ex);
                return Results.Json(new RemoteCommandResponse(false, null, ex.Message), RemoteApi.Json);
            }
        });

        app.MapPost("/api/house/keep-grouped", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteFlagBody>(ctx).ConfigureAwait(false);
            if (body is null)
                return BadJson();
            state.Settings().EnsureShape().KeepHouseGrouped = body.Enabled;
            SaveSettings(state);
            return Results.Json(new RemoteCommandResponse(true, body.Enabled ? "Keep house grouped on" : "Keep house grouped off", null), RemoteApi.Json);
        });

        app.MapGet("/api/daily-groups", () => Results.Json(BuildDaily(state), RemoteApi.Json));

        app.MapPost("/api/daily-groups/select", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteDailySelectBody>(ctx).ConfigureAwait(false);
            if (body is null)
                return BadJson();
            var model = CaptureDaily(state.Settings().EnsureShape());
            string? error = null;
            if (!string.IsNullOrWhiteSpace(body.Preset))
                error = DailyGroupLogic.SelectPreset(model, body.Preset, body.PresetOn);
            else if (body.All == true)
                DailyGroupLogic.SetAll(model, true, null);
            else if (body.All == false || body.Rooms is not null)
            {
                var seed = state.Settings().PreferredHouseCoordinatorRoom
                           ?? state.Settings().ActiveRoom
                           ?? state.Sonos.ActiveRoom;
                if (body.Rooms is { Count: > 0 })
                    error = DailyGroupLogic.SelectRooms(model, body.Rooms);
                else
                    DailyGroupLogic.SetAll(model, false, seed);
            }
            else
                return BadJson();

            if (error is not null)
                return Results.Json(new RemoteCommandResponse(false, null, error), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            StoreDaily(state, model);
            return Results.Json(new RemoteCommandResponse(true, DailyGroupLogic.ActivePresetName(model) ?? (model.AllSpeakers ? "All speakers" : $"{model.Rooms.Count} rooms"), null), RemoteApi.Json);
        });

        app.MapPost("/api/daily-groups", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteDailySaveBody>(ctx).ConfigureAwait(false);
            if (body is null)
                return BadJson();
            var model = CaptureDaily(state.Settings().EnsureShape());
            var error = DailyGroupLogic.SavePreset(model, body.Name, body.Rooms);
            if (error is not null)
                return Results.Json(new RemoteCommandResponse(false, null, error), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            StoreDaily(state, model);
            return Results.Json(new RemoteCommandResponse(true, body.Name?.Trim(), null), RemoteApi.Json);
        });

        app.MapPost("/api/daily-groups/remove", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteNameBody>(ctx).ConfigureAwait(false);
            var model = CaptureDaily(state.Settings().EnsureShape());
            var error = DailyGroupLogic.RemovePreset(model, body?.Name);
            if (error is not null)
                return Results.Json(new RemoteCommandResponse(false, null, error), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            StoreDaily(state, model);
            return Results.Json(new RemoteCommandResponse(true, "Removed", null), RemoteApi.Json);
        });

        app.MapGet("/api/library/status", () =>
        {
            var lib = state.Library;
            if (lib is null)
                return Results.Json(new RemoteLibraryStatusResponse(true, false, 0, 0, null, null, "Library is not available."), RemoteApi.Json);
            var st = lib.GetStatus();
            return Results.Json(new RemoteLibraryStatusResponse(
                true, st.IsScanning, st.TrackCount, st.SonosUnplayableCount, st.Phase, st.LastScanError, st.Note), RemoteApi.Json);
        });

        app.MapGet("/api/library/track", (HttpContext ctx) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            var lib = state.Library;
            if (lib is null || string.IsNullOrWhiteSpace(path))
                return BadJson();
            var track = lib.GetTrack(path);
            if (track is null)
                return Results.Json(new RemoteCommandResponse(false, null, "Track not in cache."), RemoteApi.Json, statusCode: StatusCodes.Status404NotFound);
            var settings = state.Settings().EnsureShape();
            return Results.Json(new RemoteSearchResponse(true, 1, [ToTrack(track, settings)]), RemoteApi.Json);
        });

        app.MapPost("/api/library/rescan", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteRescanBody>(ctx).ConfigureAwait(false) ?? new RemoteRescanBody();
            var lib = state.Library;
            if (lib is null)
                return Results.Json(new RemoteCommandResponse(false, null, "Library service not available."), RemoteApi.Json);
            var (started, message) = lib.RequestRescan(body.ForceAll, body.RediscoverRoots);
            return Results.Json(new RemoteCommandResponse(started, message, started ? null : message), RemoteApi.Json);
        });

        app.MapGet("/api/tags", () =>
        {
            var settings = state.Settings().EnsureShape();
            var tags = settings.Tags
                .Where(t => !string.IsNullOrWhiteSpace(t.Key))
                .Select(t => new RemoteTagDto(t.Key, string.IsNullOrWhiteSpace(t.Label) ? t.Key : t.Label))
                .ToList();
            return Results.Json(new RemoteTagsResponse(true, tags), RemoteApi.Json);
        });

        app.MapPost("/api/tags", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteTagCreateBody>(ctx).ConfigureAwait(false);
            var settings = state.Settings().EnsureShape();
            var tag = settings.AddTag(body?.Label ?? "");
            if (tag is null)
                return Results.Json(new RemoteCommandResponse(false, null, "Label is required."), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            SaveSettings(state);
            return Results.Json(new RemoteCommandResponse(true, tag.Label, null), RemoteApi.Json);
        });

        app.MapPost("/api/tags/rename", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteTagRenameBody>(ctx).ConfigureAwait(false);
            var settings = state.Settings().EnsureShape();
            if (body is null || !settings.RenameTag(body.Key ?? "", body.Label ?? ""))
                return Results.Json(new RemoteCommandResponse(false, null, "Unknown key or empty label."), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            SaveSettings(state);
            return Results.Json(new RemoteCommandResponse(true, body.Label?.Trim(), null), RemoteApi.Json);
        });

        app.MapPost("/api/tags/delete", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteKeyBody>(ctx).ConfigureAwait(false);
            var key = body?.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                return BadJson();
            var settings = state.Settings().EnsureShape();
            var def = settings.FindTag(key);
            TagPurgeResult? purge = state.Library?.PurgeTagKey(key);
            if (!settings.RemoveTag(key) && def is null)
                return Results.Json(new RemoteCommandResponse(false, null, "Unknown tag key."), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            SaveSettings(state);
            var message = purge is null ? "Deleted" : purge.Message;
            return Results.Json(new RemoteCommandResponse(purge?.Ok != false, message, purge?.Ok == false ? purge.Message : null), RemoteApi.Json);
        });

        app.MapPost("/api/tracks/tags", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteTagToggleBody>(ctx).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Path) || string.IsNullOrWhiteSpace(body.Key) || state.Library is null)
                return BadJson();
            var result = state.Library.SetTagFlag(body.Path.Trim(), body.Key.Trim(), body.ForceEnable);
            return Results.Json(new RemoteCommandResponse(result.Ok, result.Message, result.Ok ? null : result.Error ?? result.Message), RemoteApi.Json);
        });

        app.MapPost("/api/tracks/tags/set", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteTagSetBody>(ctx).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Path) || body.TagKeys is null || state.Library is null)
                return BadJson();
            var updateMaster = state.Settings().EnsureShape().TagUpdateMasterDefault;
            var result = state.Library.SetTags(body.Path.Trim(), new TrackTagUpdate { TagKeys = body.TagKeys }, updateMaster: updateMaster);
            return Results.Json(new RemoteCommandResponse(result.Ok, result.Message, result.Ok ? null : result.Error ?? result.Message), RemoteApi.Json);
        });

        app.MapGet("/api/reports/songs", (HttpContext ctx) =>
        {
            var (limit, offset) = Page(ctx, 40, 200);
            var rows = state.Sonos.Activity.SearchSongs(ctx.Request.Query["q"].ToString(), limit + offset);
            var page = rows.Skip(offset).Take(limit).Select(r => new RemoteSongStatDto(
                r.TrackKey, r.Title, r.Artist, r.PlayCount, r.SkipCount, r.LastPlayedLocal)).ToList();
            return Results.Json(new RemoteSongsResponse(true, page), RemoteApi.Json);
        });

        app.MapGet("/api/reports/plays", (HttpContext ctx) =>
        {
            var key = ctx.Request.Query["trackKey"].ToString();
            var (limit, offset) = Page(ctx, 40, 200);
            var rows = state.Sonos.Activity.PlaysForSong(key, limit, offset);
            return Results.Json(new RemotePlaysResponse(true, rows.Select(ToPlay).ToList()), RemoteApi.Json);
        });

        app.MapGet("/api/reports/music", (HttpContext ctx) =>
        {
            var (limit, offset) = Page(ctx, 40, 200);
            DateTime? from = ParseDay(ctx.Request.Query["from"]);
            DateTime? to = ParseDay(ctx.Request.Query["to"]);
            var skipped = string.Equals(ctx.Request.Query["skippedOnly"], "true", StringComparison.OrdinalIgnoreCase);
            var report = state.Sonos.Activity.QueryMusic(from, to, skipped, limit, offset);
            return Results.Json(new RemoteMusicResponse(
                true,
                report.Plays.Select(ToPlay).ToList(),
                report.MostPlayed.Select(ToRank).ToList(),
                report.MostSkipped.Select(ToRank).ToList(),
                report.RecentlyPlayed.Select(ToRank).ToList()), RemoteApi.Json);
        });

        app.MapGet("/api/reports/events", (HttpContext ctx) =>
        {
            var (limit, offset) = Page(ctx, 40, 200);
            var category = NullIfEmpty(ctx.Request.Query["category"]);
            var kind = NullIfEmpty(ctx.Request.Query["kind"]);
            var rows = state.Sonos.Activity.QueryEvents(category, kind, limit, offset);
            var kinds = state.Sonos.Activity.EventKinds(category);
            var events = rows.Select(e => new RemoteEventDto(e.Id, e.WhenLocal, e.CategoryLabel, e.KindLabel, e.Summary, e.Zone)).ToList();
            return Results.Json(new RemoteEventsResponse(true, events, kinds.ToList()), RemoteApi.Json);
        });

        app.MapGet("/api/heard", () =>
        {
            var rows = state.Sonos.Heard.GetRecent(40).Select(t => new RemoteHeardDto(
                t.Title ?? "",
                t.Artist ?? "",
                t.Source ?? "",
                t.Plays,
                t.LastUtc.ToLocalTime().ToString("g"))).ToList();
            return Results.Json(new RemoteHeardResponse(true, rows), RemoteApi.Json);
        });

        app.MapGet("/api/logs", (HttpContext ctx) =>
        {
            var max = 80;
            if (int.TryParse(ctx.Request.Query["max"], out var parsed))
                max = Math.Clamp(parsed, 1, 200);
            var lines = AppLog.GetRecentText(max)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .ToList();
            return Results.Json(new RemoteLogsResponse(true, lines), RemoteApi.Json);
        });

        app.MapGet("/api/settings", () => Results.Json(BuildSettings(state), RemoteApi.Json));

        app.MapPost("/api/settings", async (HttpContext ctx) =>
        {
            Log(ctx);
            var body = await ReadJson<RemoteSettingsBody>(ctx).ConfigureAwait(false);
            if (body is null)
                return BadJson();
            var error = RemoteSettingsRules.Validate(body);
            if (error is not null)
                return Results.Json(new RemoteCommandResponse(false, null, error), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            if (body.ControlShuffleSource is not null
                && body.ControlShuffleSource.Trim().StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                var path = body.ControlShuffleSource.Trim()["folder:".Length..].Trim().TrimEnd('\\', '/');
                var settingsCheck = state.Settings().EnsureShape();
                var known = settingsCheck.SonosLibraryRoots.Any(r =>
                    string.Equals(r.Trim().TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
                if (!known)
                    return Results.Json(new RemoteCommandResponse(false, null, "Folder is not a library root."), RemoteApi.Json, statusCode: StatusCodes.Status400BadRequest);
            }

            ApplySettings(state.Settings().EnsureShape(), body);
            SaveSettings(state);
            return Results.Json(new RemoteCommandResponse(true, "Saved", null), RemoteApi.Json);
        });

        app.MapPost("/api/wake/now", async (HttpContext ctx) =>
        {
            Log(ctx);
            if (state.Wake is null)
                return Results.Json(new RemoteCommandResponse(false, null, "Wake is not available."), RemoteApi.Json);
            try
            {
                await state.Wake.TriggerNowAsync().ConfigureAwait(false);
                return Results.Json(new RemoteCommandResponse(true, "Wake started", null), RemoteApi.Json);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Remote wake now failed", ex);
                return Results.Json(new RemoteCommandResponse(false, null, ex.Message), RemoteApi.Json);
            }
        });

        _ = hooks;
    }

    private static async Task<string?> StartControlShuffleAsync(HotSonosMcpState state, CancellationToken ct)
    {
        var settings = state.Settings().EnsureShape();
        var token = (settings.ControlShuffleSource ?? AppSettings.ControlShuffleAll).Trim();
        if (string.IsNullOrEmpty(token)
            || token.Equals(AppSettings.ControlShuffleAll, StringComparison.OrdinalIgnoreCase))
            return await state.ExecuteActionAsync(HotsonosAction.ShuffleLibrary).ConfigureAwait(false);

        if (AppSettings.TryParseFolderShuffleToken(token, out var folder) && state.PlayLibraryFolderAsync is not null)
            return await state.PlayLibraryFolderAsync(folder, true, ct).ConfigureAwait(false);

        if (token.StartsWith(AppSettings.ControlShuffleTagPrefix, StringComparison.OrdinalIgnoreCase)
            && state.PlayTaggedTracksAsync is not null)
            return await state.PlayTaggedTracksAsync(token[AppSettings.ControlShuffleTagPrefix.Length..].Trim(), true, ct).ConfigureAwait(false);

        if (token.StartsWith(AppSettings.ControlShuffleGenrePrefix, StringComparison.OrdinalIgnoreCase)
            && state.PlayGenreTracksAsync is not null)
            return await state.PlayGenreTracksAsync(token[AppSettings.ControlShuffleGenrePrefix.Length..].Trim(), true, ct).ConfigureAwait(false);

        return await state.ExecuteActionAsync(HotsonosAction.ShuffleLibrary).ConfigureAwait(false);
    }

    private static void ApplySettings(AppSettings settings, RemoteSettingsBody body)
    {
        if (body.VolumeStep is int step) settings.VolumeStep = step;
        if (body.LevelVolumePercent is int level) settings.LevelVolumePercent = level;
        if (body.KeepHouseGrouped is bool keep) settings.KeepHouseGrouped = keep;
        if (body.NightlyResetEnabled is bool nightly) settings.NightlyResetEnabled = nightly;
        if (body.NightlyResetMinutes is int nightlyAt) settings.NightlyResetMinutes = nightlyAt;
        if (body.NightlyResetReshuffle is bool reshuffle) settings.NightlyResetReshuffle = reshuffle;
        if (body.WakeEnabled is bool wake) settings.WakeEnabled = wake;
        if (body.WakeMinutes is int wakeAt) settings.WakeMinutes = wakeAt;
        if (body.WakeDaysMask is int days) settings.WakeDaysMask = days;
        if (body.WakeRoom is not null) settings.WakeRoom = string.IsNullOrWhiteSpace(body.WakeRoom) ? null : body.WakeRoom.Trim();
        if (body.WakeSource is not null) settings.WakeSource = RemoteSettingsRules.CanonicalWakeSource(body.WakeSource);
        if (body.WakeFavoriteName is not null) settings.WakeFavoriteName = string.IsNullOrWhiteSpace(body.WakeFavoriteName) ? null : body.WakeFavoriteName.Trim();
        if (body.WakeStartVolume is int start) settings.WakeStartVolume = start;
        if (body.WakeEndVolume is int end) settings.WakeEndVolume = end;
        if (body.WakeVolumeStep is int wstep) settings.WakeVolumeStep = wstep;
        if (body.WakeStepIntervalMinutes is int interval) settings.WakeStepIntervalMinutes = interval;
        if (body.WakeExpandToHouse is bool expand) settings.WakeExpandToHouse = expand;
        if (body.GenaEnabled is bool gena) settings.GenaEnabled = gena;
        if (body.ShowGenres is bool genres) settings.ShowGenresInPlaySources = genres;
        if (body.ShuffleExcludePlayed is bool exclude) settings.ShuffleExcludePlayed = exclude;
        if (body.ShuffleAutoTopUp is bool topUp) settings.ShuffleAutoTopUp = topUp;
        if (body.ContinueAfterSpecial is bool cont) settings.ContinueLibraryShuffleAfterSpecialPlay = cont;
        if (body.ShuffleArtistSpread is bool spread) settings.ShuffleArtistSpread = spread;
        if (body.ShuffleQueueTracks is int queue) settings.ShuffleQueueTracks = queue;
        if (body.ShuffleTopUpTracks is int top) settings.ShuffleTopUpTracks = top;
        if (body.ShuffleHistoryDays is int history) settings.ShuffleHistoryDays = history;
        if (body.ShuffleTopUpWhenRemaining is int remaining) settings.ShuffleTopUpWhenRemaining = remaining;
        if (body.TagUpdateMaster is bool master) settings.TagUpdateMasterDefault = master;
        if (body.ControlShuffleSource is not null) settings.ControlShuffleSource = body.ControlShuffleSource.Trim();
        if (body.PreferredCoordinator is not null)
            settings.PreferredHouseCoordinatorRoom = string.IsNullOrWhiteSpace(body.PreferredCoordinator) ? null : body.PreferredCoordinator.Trim();
    }

    private static RemoteSettingsResponse BuildSettings(HotSonosMcpState state)
    {
        var s = state.Settings().EnsureShape();
        var source = (s.ControlShuffleSource ?? "all").Trim();
        return new RemoteSettingsResponse(
            true,
            s.VolumeStep,
            s.LevelVolumePercent,
            s.KeepHouseGrouped,
            s.NightlyResetEnabled,
            s.NightlyResetMinutes,
            s.NightlyResetReshuffle,
            s.WakeEnabled,
            s.WakeMinutes,
            s.WakeDaysMask,
            s.WakeRoom,
            s.WakeSource,
            s.WakeFavoriteName,
            s.WakeStartVolume,
            s.WakeEndVolume,
            s.WakeVolumeStep,
            s.WakeStepIntervalMinutes,
            s.WakeExpandToHouse,
            state.Wake?.IsActive == true,
            s.GenaEnabled,
            s.ShowGenresInPlaySources,
            s.ShuffleExcludePlayed,
            s.ShuffleAutoTopUp,
            s.ContinueLibraryShuffleAfterSpecialPlay,
            s.ShuffleArtistSpread,
            s.ShuffleQueueTracks,
            s.ShuffleTopUpTracks,
            s.ShuffleHistoryDays,
            s.ShuffleTopUpWhenRemaining,
            s.TagUpdateMasterDefault,
            source,
            DescribeShuffleSource(s, source),
            s.PreferredHouseCoordinatorRoom);
    }

    private static RemoteTopologyResponse BuildTopology(HotSonosMcpState state)
    {
        var settings = state.Settings().EnsureShape();
        var snap = state.Sonos.LastTopology;
        var members = snap?.Members.Select(m => new RemoteMemberDto(
            m.RoomName,
            m.IpAddress,
            m.Uuid,
            m.Invisible,
            m.ChannelRole,
            m.ProductName,
            m.IsCoordinator,
            m.GroupId)).ToList() ?? [];
        var coordinators = state.Sonos.GetCoordinatorCandidates()
            .Select(c => new RemoteCoordinatorDto(c.Room, c.Ip, c.IsLeading))
            .ToList();
        return new RemoteTopologyResponse(
            true,
            state.Sonos.ActiveRoom,
            settings.PreferredHouseCoordinatorRoom,
            settings.KeepHouseGrouped,
            state.Sonos.Groups.Select(g => new RemoteGroupDto(g.CoordinatorRoom, g.DisplayName, g.MemberCount)).ToList(),
            members,
            coordinators,
            state.Sonos.OfflineSpeakers.ToList(),
            snap?.VanishedRooms.ToList() ?? [],
            snap is null ? "No topology snapshot yet." : null);
    }

    private static RemoteDailyGroupsResponse BuildDaily(HotSonosMcpState state)
    {
        var settings = state.Settings().EnsureShape();
        var model = CaptureDaily(settings);
        var active = DailyGroupLogic.ActivePresetName(model);
        var known = KnownRooms(state);
        var presets = model.Presets.Select(p => new RemotePresetDto(
            p.Name,
            p.Rooms,
            !model.AllSpeakers && string.Equals(p.Name, active, StringComparison.OrdinalIgnoreCase))).ToList();
        return new RemoteDailyGroupsResponse(true, model.AllSpeakers, model.Rooms, known, presets, active, null);
    }

    private static List<string> KnownRooms(HotSonosMcpState state)
    {
        var rooms = state.Sonos.GetCoordinatorCandidates()
            .Select(c => c.Room)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (rooms.Count == 0 && state.Sonos.LastTopology is { } snap)
        {
            rooms = snap.Members
                .Where(m => !m.Invisible && !string.IsNullOrWhiteSpace(m.RoomName))
                .Select(m => m.RoomName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return rooms;
    }

    private static DailyGroupModel CaptureDaily(AppSettings settings) => new()
    {
        AllSpeakers = settings.DailyGroupAllSpeakers,
        Rooms = settings.DailyGroupRooms?.ToList() ?? [],
        Presets = (settings.DailySpeakerPresets ?? [])
            .Select(p => new DailyPresetModel { Name = p.Name, Rooms = p.Rooms?.ToList() ?? [] })
            .ToList(),
    };

    private static void StoreDaily(HotSonosMcpState state, DailyGroupModel model)
    {
        var settings = state.Settings().EnsureShape();
        settings.DailyGroupAllSpeakers = model.AllSpeakers;
        settings.DailyGroupRooms = model.Rooms.ToList();
        settings.DailySpeakerPresets = model.Presets
            .Select(p => new DailySpeakerPreset { Name = p.Name, Rooms = p.Rooms.ToList() })
            .ToList();
        SaveSettings(state);
    }

    private static void SaveSettings(HotSonosMcpState state)
    {
        try { state.PersistSettings?.Invoke(); }
        catch (Exception ex) { AppLog.Warn("Remote settings save failed", ex); }
        try { state.AfterSettingsChanged?.Invoke(); }
        catch (Exception ex) { AppLog.Warn("Remote settings refresh failed", ex); }
    }

    private static (string Room, string Ip)? FindZone(HotSonosMcpState state, string? room, string? ip)
    {
        var zones = state.Sonos.GetZoneEndpoints();
        foreach (var zone in zones)
        {
            if (!string.IsNullOrWhiteSpace(ip) && string.Equals(zone.Ip, ip.Trim(), StringComparison.OrdinalIgnoreCase))
                return zone;
            if (!string.IsNullOrWhiteSpace(room) && string.Equals(zone.Room, room.Trim(), StringComparison.OrdinalIgnoreCase))
                return zone;
        }

        return null;
    }

    /// <summary>Same order as Topology → Restart all: coordinator last, one reboot per IP.</summary>
    private static async Task<string> RebootAllAsync(HotSonosMcpState state, CancellationToken ct)
    {
        var snap = state.Sonos.LastTopology;
        if (snap is null || snap.Members.Count == 0)
            return "No topology yet. Refresh devices first.";

        var players = snap.Members
            .Where(m => !string.IsNullOrWhiteSpace(m.IpAddress))
            .GroupBy(m => m.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.IsCoordinator && !m.Invisible)
            .ThenBy(m => m.RoomName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ok = 0;
        var skipped = new List<string>();
        foreach (var player in players)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await state.Sonos.RebootPlayerAsync(player.IpAddress, ct).ConfigureAwait(false);
                ok++;
            }
            catch (Exception ex)
            {
                skipped.Add(player.RoomName);
                AppLog.Warn($"Remote reboot {player.RoomName} failed", ex);
            }

            await Task.Delay(600, ct).ConfigureAwait(false);
        }

        var tail = skipped.Count == 0 ? "" : " Skipped: " + string.Join(", ", skipped) + ".";
        return $"Reboot requested for {ok}/{players.Count} speaker(s).{tail}";
    }

    private static string DescribeShuffleSource(AppSettings settings, string token)
    {
        if (token.Equals(AppSettings.ControlShuffleAll, StringComparison.OrdinalIgnoreCase))
            return "All · Music Library";
        if (AppSettings.TryParseFolderShuffleToken(token, out var folder))
        {
            var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
            return "Folder · " + (string.IsNullOrWhiteSpace(name) ? folder : name);
        }

        if (token.StartsWith(AppSettings.ControlShuffleTagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var key = token[AppSettings.ControlShuffleTagPrefix.Length..].Trim();
            return "Tag · " + settings.TagLabel(key);
        }

        if (token.StartsWith(AppSettings.ControlShuffleGenrePrefix, StringComparison.OrdinalIgnoreCase))
            return "Genre · " + token[AppSettings.ControlShuffleGenrePrefix.Length..].Trim();
        return token;
    }

    private static RemoteTrackDto ToTrack(LibraryTrack t, AppSettings settings) => new(
        t.Path, t.Title, t.Artist, t.Album, t.Genre, t.FormatTagLabels(settings.TagLabel));

    private static RemotePlayRowDto ToPlay(HotSonos.Activity.PlaySessionRow r) => new(
        r.TrackKey, r.Title, r.Artist, r.WhenLocal, r.Zone, r.OutcomeLabel, r.TriggerLabel, r.Listened, r.Length);

    private static RemoteRankDto ToRank(HotSonos.Activity.RankedSongRow r) =>
        new(r.Title, r.Artist, r.Count, r.Label);

    private static (int Limit, int Offset) Page(HttpContext ctx, int fallback, int max)
    {
        var limit = fallback;
        if (int.TryParse(ctx.Request.Query["limit"], out var parsed))
            limit = Math.Clamp(parsed, 1, max);
        var offset = 0;
        if (int.TryParse(ctx.Request.Query["offset"], out var parsedOffset) && parsedOffset > 0)
            offset = parsedOffset;
        return (limit, offset);
    }

    private static DateTime? ParseDay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)
            ? day.Date
            : null;
    }

    private static string? NullIfEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
