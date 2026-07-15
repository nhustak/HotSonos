using System.ComponentModel;
using System.Text.Json;
using HotSonos.App.Infrastructure;
using HotSonos.App.Models;
using HotSonos.App.Services;
using ModelContextProtocol.Server;

namespace HotSonos.App.Mcp;

/// <summary>
/// Debug / ops tools for agents. Requires the HotSonos tray app to be running
/// with MCP enabled (loopback only).
/// </summary>
[McpServerToolType]
public sealed class HotSonosDebugTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly HotSonosMcpState _state;

    public HotSonosDebugTools(HotSonosMcpState state) => _state = state;

    [McpServerTool(Name = "get_status")]
    [Description("HotSonos app status: version, MCP endpoint, whether the device list is populated, active room, groups, offline, wake, playing, now-playing.")]
    public async Task<string> GetStatus(CancellationToken ct)
    {
        var s = _state.Settings().EnsureShape();
        var sonos = _state.Sonos;
        var playing = false;
        try { playing = await sonos.IsAnythingPlayingAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn("MCP IsAnythingPlaying failed", ex); }

        var zoneCount = sonos.GetZoneCount();
        var groupCount = sonos.Groups.Count;
        var payload = new
        {
            version = AppVersion.Current,
            mcp = new { running = _state.IsRunning, endpoint = _state.Endpoint, port = s.McpPort, enabled = s.McpEnabled },
            deviceListPopulated = groupCount > 0,
            zoneCount,
            groupCount,
            activeRoom = sonos.ActiveRoom,
            activeGroupLabel = sonos.ActiveGroupLabel,
            offline = sonos.OfflineSpeakers,
            offlineCount = sonos.OfflineSpeakers.Count,
            wakeActive = _state.Wake?.IsActive == true,
            wakeEnabled = s.WakeEnabled,
            wakeNextFireLocal = _state.Wake?.GetNextFireLocal()?.ToString("yyyy-MM-dd HH:mm"),
            anythingPlaying = playing,
            lastNowPlaying = FormatNowPlaying(_state.GetLastNowPlaying()),
            hint = groupCount == 0
                ? "Device list empty — call refresh_devices, then list_groups / list_zones. Check get_logs if still empty."
                : "Device list has groups; use list_groups or list_zones for details.",
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    [McpServerTool(Name = "get_discovery_state")]
    [Description("Whether HotSonos has a populated device list (groups/zones), plus offline rooms and active target. Use first when debugging empty Settings lists.")]
    public string GetDiscoveryState()
    {
        var sonos = _state.Sonos;
        var zoneCount = sonos.GetZoneCount();
        var groupCount = sonos.Groups.Count;
        return JsonSerializer.Serialize(new
        {
            deviceListPopulated = groupCount > 0,
            zoneCount,
            groupCount,
            activeRoom = sonos.ActiveRoom,
            groups = sonos.Groups.Select(g => g.DisplayName).ToList(),
            offline = sonos.OfflineSpeakers,
            populated = groupCount > 0,
            message = groupCount > 0
                ? $"Populated: {groupCount} group(s), {zoneCount} zone(s)."
                : "Not populated: no groups in cache. Call refresh_devices.",
        }, JsonOptions);
    }

    [McpServerTool(Name = "list_groups")]
    [Description("List Sonos groups currently known to HotSonos (display name, coordinator room, uuid, ip, member count). Empty if discovery has not succeeded.")]
    public string ListGroups()
    {
        var groups = _state.Sonos.Groups.Select(g => new
        {
            g.DisplayName,
            g.CoordinatorRoom,
            g.CoordinatorUuid,
            g.CoordinatorIp,
            g.MemberCount,
            isActive = string.Equals(g.CoordinatorRoom, _state.Sonos.ActiveRoom, StringComparison.OrdinalIgnoreCase),
        });
        return JsonSerializer.Serialize(new
        {
            count = _state.Sonos.Groups.Count,
            activeRoom = _state.Sonos.ActiveRoom,
            groups,
        }, JsonOptions);
    }

    [McpServerTool(Name = "list_zones")]
    [Description("List individual visible zones/players (room, ip, uuid, coordinator). Use when the Settings device list looks empty or stale.")]
    public string ListZones()
    {
        var snap = _state.Sonos.GetTopologySnapshot();
        return JsonSerializer.Serialize(snap, JsonOptions);
    }

    [McpServerTool(Name = "list_offline")]
    [Description("Rooms Sonos currently reports as vanished/offline.")]
    public string ListOffline()
    {
        var offline = _state.Sonos.OfflineSpeakers;
        return JsonSerializer.Serialize(new { count = offline.Count, rooms = offline }, JsonOptions);
    }

    [McpServerTool(Name = "refresh_devices")]
    [Description("Run SSDP discovery + topology refresh (same as Settings auto-refresh / Refresh devices). Returns group count and any error.")]
    public async Task<string> RefreshDevices(CancellationToken ct)
    {
        try
        {
            var message = await _state.RefreshDevicesAsync().ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                message,
                groupCount = _state.Sonos.Groups.Count,
                activeRoom = _state.Sonos.ActiveRoom,
                groups = _state.Sonos.Groups.Select(g => g.DisplayName).ToList(),
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            AppLog.Error("MCP refresh_devices failed", ex);
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool(Name = "get_speaker_volumes")]
    [Description("Live volume/mute for every visible speaker (SOAP GetVolume/GetMute).")]
    public async Task<string> GetSpeakerVolumes(CancellationToken ct)
    {
        try
        {
            var volumes = await _state.Sonos.GetSpeakerVolumesAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                count = volumes.Count,
                speakers = volumes.Select(v => new
                {
                    v.RoomName,
                    v.IpAddress,
                    v.Volume,
                    v.Muted,
                    v.Reachable,
                }),
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool(Name = "get_now_playing")]
    [Description("Last now-playing snapshot HotSonos received from GENA (may be empty if not subscribed yet).")]
    public string GetNowPlaying()
    {
        var np = _state.GetLastNowPlaying();
        return JsonSerializer.Serialize(new
        {
            hasData = np is not null && !np.IsEmpty,
            nowPlaying = FormatNowPlaying(np),
        }, JsonOptions);
    }

    [McpServerTool(Name = "list_favorites")]
    [Description("Browse Sonos favorites (FV:2) and playlists (SQ:) from the active coordinator. Requires discovery.")]
    public async Task<string> ListFavorites(CancellationToken ct)
    {
        try
        {
            var list = await _state.Sonos.GetFavoritesAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                count = list.Count,
                items = list.Select(f => new
                {
                    f.Id,
                    kind = f.Kind.ToString(),
                    f.Title,
                    f.IsPlayable,
                    hasUri = !string.IsNullOrWhiteSpace(f.Uri),
                }),
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool(Name = "get_settings_summary")]
    [Description("Safe subset of AppSettings (rooms, wake, MCP, volume steps — no secrets).")]
    public string GetSettingsSummary()
    {
        var s = _state.Settings().EnsureShape();
        return JsonSerializer.Serialize(new
        {
            s.ActiveRoom,
            s.VolumeStep,
            s.LevelVolumePercent,
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
            s.McpEnabled,
            s.McpPort,
            favoriteSlots = s.FavoriteSlots.Select((f, i) => new { slot = i + 1, f.FavoriteName, hotkey = f.Hotkey.ToString() }),
        }, JsonOptions);
    }

    [McpServerTool(Name = "get_logs")]
    [Description("Recent HotSonos log lines from the in-memory ring (also written under %LocalAppData%\\HotSonos\\logs).")]
    public string GetLogs(
        [Description("Max lines to return (default 100, max 500)")] int maxLines = 100)
    {
        maxLines = Math.Clamp(maxLines, 1, 500);
        return AppLog.GetRecentText(maxLines);
    }

    [McpServerTool(Name = "get_log_directory")]
    [Description("Absolute path to the HotSonos log directory on this machine.")]
    public string GetLogDirectory() => AppLog.DirectoryPath;

    // ---- Control (same actions as tray / hotkeys) -------------------------

    [McpServerTool(Name = "play_pause")]
    [Description("Toggle play/pause on the active Sonos group.")]
    public Task<string> PlayPause(CancellationToken ct) => RunActionAsync(HotsonosAction.PlayPause, ct);

    [McpServerTool(Name = "next_track")]
    [Description("Skip to next track on the active group.")]
    public Task<string> NextTrack(CancellationToken ct) => RunActionAsync(HotsonosAction.Next, ct);

    [McpServerTool(Name = "previous_track")]
    [Description("Go to previous track on the active group.")]
    public Task<string> PreviousTrack(CancellationToken ct) => RunActionAsync(HotsonosAction.Previous, ct);

    [McpServerTool(Name = "volume_up")]
    [Description("Raise group volume by the configured step (cancels wake ramp if active).")]
    public Task<string> VolumeUp(CancellationToken ct) => RunActionAsync(HotsonosAction.VolumeUp, ct);

    [McpServerTool(Name = "volume_down")]
    [Description("Lower group volume by the configured step (cancels wake ramp if active).")]
    public Task<string> VolumeDown(CancellationToken ct) => RunActionAsync(HotsonosAction.VolumeDown, ct);

    [McpServerTool(Name = "mute_toggle")]
    [Description("Toggle mute on the active group (cancels wake ramp if active).")]
    public Task<string> MuteToggle(CancellationToken ct) => RunActionAsync(HotsonosAction.Mute, ct);

    [McpServerTool(Name = "level_volumes")]
    [Description("Set all speakers to the configured level volume percent and unmute (cancels wake ramp if active).")]
    public Task<string> LevelVolumes(CancellationToken ct) => RunActionAsync(HotsonosAction.LevelVolumes, ct);

    [McpServerTool(Name = "shuffle_library")]
    [Description("Group all speakers under the active coordinator and client-side shuffle the full Music Library.")]
    public Task<string> ShuffleLibrary(CancellationToken ct) => RunActionAsync(HotsonosAction.ShuffleLibrary, ct);

    [McpServerTool(Name = "fresh_start")]
    [Description("Re-discover, regroup all speakers, and shuffle the library (Fresh Start).")]
    public Task<string> FreshStart(CancellationToken ct) => RunActionAsync(HotsonosAction.FreshStart, ct);

    [McpServerTool(Name = "play_favorite_slot")]
    [Description("Play favorite/playlist hotkey slot 1-4 (must be assigned in Settings).")]
    public Task<string> PlayFavoriteSlot(
        [Description("Slot number 1 through 4")] int slot,
        CancellationToken ct)
    {
        var action = slot switch
        {
            1 => HotsonosAction.Favorite1,
            2 => HotsonosAction.Favorite2,
            3 => HotsonosAction.Favorite3,
            4 => HotsonosAction.Favorite4,
            _ => throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1-4."),
        };
        return RunActionAsync(action, ct);
    }

    [McpServerTool(Name = "set_active_room")]
    [Description("Set the active target room/group by coordinator room name (same keys as list_groups.coordinatorRoom).")]
    public string SetActiveRoom(
        [Description("Coordinator room name, e.g. Office or Living Room")] string room)
    {
        if (string.IsNullOrWhiteSpace(room))
            return JsonSerializer.Serialize(new { ok = false, error = "room is required" }, JsonOptions);

        var group = _state.Sonos.TryGetGroup(room);
        if (group is null && _state.Sonos.Groups.Count > 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Room '{room}' not found in current discovery.",
                available = _state.Sonos.Groups.Select(g => g.CoordinatorRoom).ToList(),
            }, JsonOptions);
        }

        _state.SetActiveRoom?.Invoke(room.Trim());
        return JsonSerializer.Serialize(new
        {
            ok = true,
            activeRoom = _state.Sonos.ActiveRoom,
            activeGroupLabel = _state.Sonos.ActiveGroupLabel,
        }, JsonOptions);
    }

    [McpServerTool(Name = "wake_now")]
    [Description("Start wake-to-music immediately using current Settings (skips if anything is already playing). Returns right away; ramp may run for many minutes. Use wake_cancel to stop the ramp.")]
    public string WakeNow()
    {
        if (_state.Wake is null)
            return JsonSerializer.Serialize(new { ok = false, error = "Wake service not available" }, JsonOptions);

        _ = Task.Run(async () =>
        {
            try { await _state.Wake.TriggerNowAsync().ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Error("MCP wake_now background failed", ex); }
        });

        return JsonSerializer.Serialize(new
        {
            ok = true,
            message = "Wake started in background (or will skip if music is already playing). Check get_status / get_logs.",
            wakeNextFireLocal = _state.Wake.GetNextFireLocal()?.ToString("yyyy-MM-dd HH:mm"),
        }, JsonOptions);
    }

    [McpServerTool(Name = "wake_cancel")]
    [Description("Cancel an in-progress wake volume ramp / expand (does not stop Sonos playback).")]
    public string WakeCancel()
    {
        if (_state.Wake is null)
            return JsonSerializer.Serialize(new { ok = false, error = "Wake service not available" }, JsonOptions);
        _state.Wake.Cancel();
        return JsonSerializer.Serialize(new { ok = true, message = "Wake cancel requested", wakeActive = _state.Wake.IsActive }, JsonOptions);
    }

    private async Task<string> RunActionAsync(HotsonosAction action, CancellationToken ct)
    {
        try
        {
            AppLog.Info($"MCP action {action}");
            var toast = await _state.ExecuteActionAsync(action).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                action = action.ToString(),
                toast,
                activeRoom = _state.Sonos.ActiveRoom,
                deviceListPopulated = _state.Sonos.Groups.Count > 0,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            AppLog.Error($"MCP action {action} failed", ex);
            return JsonSerializer.Serialize(new
            {
                ok = false,
                action = action.ToString(),
                error = ex.Message,
                deviceListPopulated = _state.Sonos.Groups.Count > 0,
            }, JsonOptions);
        }
    }

    private static object? FormatNowPlaying(HotSonos.Core.Models.NowPlaying? np)
    {
        if (np is null) return null;
        return new
        {
            np.Title,
            np.Artist,
            np.Album,
            state = np.State.ToString(),
            np.AlbumArtUri,
            np.IsEmpty,
            np.DisplayLine,
        };
    }
}
