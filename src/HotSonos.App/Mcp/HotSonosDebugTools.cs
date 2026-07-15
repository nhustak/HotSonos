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
    [Description("HotSonos app status: version, MCP endpoint, active room, group/offline counts, wake flag, whether anything is playing.")]
    public async Task<string> GetStatus(CancellationToken ct)
    {
        var s = _state.Settings().EnsureShape();
        var sonos = _state.Sonos;
        var playing = false;
        try { playing = await sonos.IsAnythingPlayingAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn("MCP IsAnythingPlaying failed", ex); }

        var payload = new
        {
            version = AppVersion.Current,
            mcp = new { running = _state.IsRunning, endpoint = _state.Endpoint, port = s.McpPort, enabled = s.McpEnabled },
            activeRoom = sonos.ActiveRoom,
            activeGroupLabel = sonos.ActiveGroupLabel,
            groupCount = sonos.Groups.Count,
            offline = sonos.OfflineSpeakers,
            wakeActive = _state.Wake?.IsActive == true,
            wakeEnabled = s.WakeEnabled,
            anythingPlaying = playing,
            lastNowPlaying = FormatNowPlaying(_state.GetLastNowPlaying()),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
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
