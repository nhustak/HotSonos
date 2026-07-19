using System.ComponentModel;
using System.Text.Json;
using HotSonos.App.Infrastructure;
using HotSonos.App.Library;
using HotSonos.App.Models;
using HotSonos.App.Services;
using ModelContextProtocol.Server;

namespace HotSonos.App.Mcp;

/// <summary>
/// Debug / ops tools for agents. Requires the HotSonos tray app to be running
/// with MCP enabled (loopback only). Every tool call is recorded for the MCP Debug tab.
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
    public Task<string> GetStatus(CancellationToken ct) =>
        McpActivityLog.RunAsync("get_status", null, async () =>
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
        });

    [McpServerTool(Name = "get_discovery_state")]
    [Description("Whether HotSonos has a populated device list (groups/zones), plus offline rooms and active target. Use first when debugging empty Settings lists.")]
    public string GetDiscoveryState() =>
        McpActivityLog.Run("get_discovery_state", null, () =>
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
        });

    [McpServerTool(Name = "list_groups")]
    [Description("List Sonos groups currently known to HotSonos (display name, coordinator room, uuid, ip, member count). Empty if discovery has not succeeded.")]
    public string ListGroups() =>
        McpActivityLog.Run("list_groups", null, () =>
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
        });

    [McpServerTool(Name = "list_zones")]
    [Description("List individual visible zones/players (room, ip, uuid, coordinator). Use when the Settings device list looks empty or stale.")]
    public string ListZones() =>
        McpActivityLog.Run("list_zones", null, () =>
        {
            var snap = _state.Sonos.GetTopologySnapshot();
            return JsonSerializer.Serialize(snap, JsonOptions);
        });

    [McpServerTool(Name = "list_offline")]
    [Description("Rooms Sonos currently reports as vanished/offline.")]
    public string ListOffline() =>
        McpActivityLog.Run("list_offline", null, () =>
        {
            var offline = _state.Sonos.OfflineSpeakers;
            return JsonSerializer.Serialize(new { count = offline.Count, rooms = offline }, JsonOptions);
        });

    [McpServerTool(Name = "refresh_devices")]
    [Description("Run SSDP discovery + topology refresh (same as Settings auto-refresh / Refresh devices). Returns group count and any error.")]
    public Task<string> RefreshDevices(CancellationToken ct) =>
        McpActivityLog.RunAsync("refresh_devices", null, async () =>
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
        });

    [McpServerTool(Name = "get_speaker_volumes")]
    [Description("Live volume/mute for every visible speaker (SOAP GetVolume/GetMute).")]
    public Task<string> GetSpeakerVolumes(CancellationToken ct) =>
        McpActivityLog.RunAsync("get_speaker_volumes", null, async () =>
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
        });

    [McpServerTool(Name = "get_now_playing")]
    [Description("Last now-playing snapshot HotSonos received from GENA (may be empty if not subscribed yet).")]
    public string GetNowPlaying() =>
        McpActivityLog.Run("get_now_playing", null, () =>
        {
            var np = _state.GetLastNowPlaying();
            return JsonSerializer.Serialize(new
            {
                hasData = np is not null && !np.IsEmpty,
                nowPlaying = FormatNowPlaying(np),
            }, JsonOptions);
        });

    [McpServerTool(Name = "list_favorites")]
    [Description("Browse Sonos favorites (FV:2) and playlists (SQ:) from the active coordinator. Requires discovery.")]
    public Task<string> ListFavorites(CancellationToken ct) =>
        McpActivityLog.RunAsync("list_favorites", null, async () =>
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
        });

    [McpServerTool(Name = "get_settings_summary")]
    [Description("Safe subset of AppSettings (rooms, wake, MCP, library roots, volume steps — no secrets).")]
    public string GetSettingsSummary() =>
        McpActivityLog.Run("get_settings_summary", null, () =>
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
                s.ShuffleQueueTracks,
                s.ShuffleTopUpTracks,
                s.ShuffleHistoryDays,
                s.ShuffleTopUpWhenRemaining,
                s.ShuffleExcludePlayed,
                s.ShuffleAutoTopUp,
                s.ShuffleArtistSpread,
                playHistoryDistinct = _state.Sonos.PlayHistory.PlayedDistinctCount,
                sonosLibraryRoots = s.SonosLibraryRoots,
                s.MasterLibraryRoot,
                favoriteSlots = s.FavoriteSlots.Select((f, i) => new { slot = i + 1, f.FavoriteName, hotkey = f.Hotkey.ToString() }),
            }, JsonOptions);
        });

    [McpServerTool(Name = "get_library_config")]
    [Description("Configured Sonos library root path(s) and optional master library root (filesystem).")]
    public string GetLibraryConfig() =>
        McpActivityLog.Run("get_library_config", null, () =>
        {
            var s = _state.Settings().EnsureShape();
            var roots = s.SonosLibraryRoots;
            var status = _state.Library?.GetStatus();
            return JsonSerializer.Serialize(new
            {
                sonosLibraryRoots = roots,
                sonosRootCount = roots.Count,
                masterLibraryRoot = s.MasterLibraryRoot,
                configured = roots.Count > 0 || !string.IsNullOrWhiteSpace(s.MasterLibraryRoot),
                trackCount = status?.TrackCount ?? 0,
                isScanning = status?.IsScanning ?? false,
                databasePath = status?.DatabasePath,
                note = "Roots + SQLite cache (step 2). Tag write / full MCP library is later. Daily shuffle still uses Sonos A:TRACKS.",
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "get_library_status")]
    [Description("Library cache status: track count, scan progress, last scan stats, DB path, configured roots.")]
    public string GetLibraryStatus() =>
        McpActivityLog.Run("get_library_status", null, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            return JsonSerializer.Serialize(lib.GetStatus(), JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "discover_library_roots")]
    [Description("Discover Music Library filesystem UNC roots from Sonos A:TRACKS (x-file-cifs URIs). Saves into settings.")]
    public Task<string> DiscoverLibraryRoots(CancellationToken ct) =>
        McpActivityLog.RunAsync("discover_library_roots", null, async () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            var (ok, message, roots) = await lib.DiscoverRootsFromSonosAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                ok,
                message,
                roots,
                status = lib.GetStatus(),
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "library_rescan")]
    [Description("Start a background scan into SQLite (FLAC/MP3 tags). If roots are empty (or rediscoverRoots=true), discovers roots from Sonos A:TRACKS first.")]
    public string LibraryRescan(
        [Description("If true, re-read tags for every file even when size/mtime match.")] bool forceAll = false,
        [Description("If true, re-discover roots from Sonos before scanning.")] bool rediscoverRoots = false) =>
        McpActivityLog.Run("library_rescan", new { forceAll, rediscoverRoots }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            var (started, message) = lib.RequestRescan(forceAll, rediscoverRoots);
            return JsonSerializer.Serialize(new
            {
                ok = started,
                started,
                message,
                forceAll,
                rediscoverRoots,
                status = lib.GetStatus(),
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "library_search")]
    [Description("Search library cache (title/artist/album/genre/tempo/codec/path). Includes bit depth, sample rate, bitrate, SonosPlayable heuristic. Max 200.")]
    public string LibrarySearch(
        [Description("Substring match; empty = browse")] string? query = null,
        [Description("Max rows (default 25, max 200)")] int limit = 25,
        [Description("Offset for paging")] int offset = 0,
        [Description("If true, only tracks flagged as outside Sonos local-library format limits")] bool sonosUnplayableOnly = false) =>
        McpActivityLog.Run("library_search", new { query, limit, offset, sonosUnplayableOnly }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            limit = Math.Clamp(limit, 1, 200);
            offset = Math.Max(0, offset);
            var tracks = lib.Search(query, limit, offset, sonosUnplayableOnly);
            var st = lib.GetStatus();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                query,
                limit,
                offset,
                sonosUnplayableOnly,
                count = tracks.Count,
                trackCountTotal = st.TrackCount,
                sonosUnplayableCount = st.SonosUnplayableCount,
                tracks = tracks.Select(t => new
                {
                    t.Path,
                    t.Title,
                    t.Artist,
                    t.Album,
                    t.AlbumArtist,
                    t.Genre,
                    t.TrackNumber,
                    t.Year,
                    t.DurationMs,
                    t.Tempo,
                    t.Bpm,
                    t.Codec,
                    t.SampleRateHz,
                    t.BitsPerSample,
                    t.Channels,
                    t.BitrateKbps,
                    audio = t.AudioFormatLabel,
                    t.SonosPlayable,
                    t.SonosPlayIssue,
                    t.RelativePath,
                }),
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "library_get_track")]
    [Description("Get one cached track by full filesystem path.")]
    public string LibraryGetTrack(
        [Description("Absolute path to the audio file")] string path) =>
        McpActivityLog.Run("library_get_track", new { path }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            var track = lib.GetTrack(path);
            if (track is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Track not in cache.", path }, JsonOptions);

            return JsonSerializer.Serialize(new { ok = true, track }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "track_set_tags")]
    [Description("Write tags into a FLAC/MP3 on the Sonos library share (HOTSONOS_TEMPO and/or standard fields). Updates SQLite cache after write. Path must be under configured roots. dryRun=true previews changes.")]
    public string TrackSetTags(
        [Description("Absolute/UNC path to the audio file (same as library_search path)")] string path,
        [Description("slow | medium | fast, or empty string to clear HOTSONOS_TEMPO")] string? tempo = null,
        [Description("Title (null = leave unchanged)")] string? title = null,
        [Description("Artist (null = leave unchanged)")] string? artist = null,
        [Description("Album (null = leave unchanged)")] string? album = null,
        [Description("Genre (null = leave unchanged)")] string? genre = null,
        [Description("Track number (null = leave unchanged)")] int? trackNumber = null,
        [Description("Year (null = leave unchanged)")] int? year = null,
        [Description("BPM (null = leave unchanged)")] double? bpm = null,
        [Description("If true, do not write the file — return planned changes only")] bool dryRun = false) =>
        McpActivityLog.Run("track_set_tags", new { path, tempo, title, artist, album, genre, trackNumber, year, bpm, dryRun }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            // Only pass fields that were explicitly intended: MCP may send default nulls.
            // Convention: any non-null string (including "") is intentional; tempo "" clears.
            var update = new TrackTagUpdate
            {
                Tempo = tempo,
                Title = title,
                Artist = artist,
                Album = album,
                Genre = genre,
                TrackNumber = trackNumber,
                Year = year,
                Bpm = bpm,
            };

            // If caller only wants dry-run probe with no fields, still call for validation.
            var result = lib.SetTags(path, update, dryRun);
            return JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                dryRun = result.DryRun,
                path = result.Path,
                message = result.Message,
                error = result.Error,
                changes = result.Changes,
                track = result.TrackAfter is null ? null : new
                {
                    result.TrackAfter.Path,
                    result.TrackAfter.Title,
                    result.TrackAfter.Artist,
                    result.TrackAfter.Album,
                    result.TrackAfter.Genre,
                    result.TrackAfter.TrackNumber,
                    result.TrackAfter.Year,
                    result.TrackAfter.Tempo,
                    result.TrackAfter.Bpm,
                    result.TrackAfter.AudioFormatLabel,
                    result.TrackAfter.SonosPlayable,
                    result.TrackAfter.SonosPlayIssue,
                },
                note = "Master dual-write is step 4 (not yet). Tags are written into the Sonos-library file only.",
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "get_logs")]
    [Description("Recent HotSonos log lines from the in-memory ring (also written under %LocalAppData%\\HotSonos\\logs).")]
    public string GetLogs(
        [Description("Max lines to return (default 100, max 500)")] int maxLines = 100) =>
        McpActivityLog.Run("get_logs", new { maxLines }, () =>
        {
            maxLines = Math.Clamp(maxLines, 1, 500);
            return AppLog.GetRecentText(maxLines);
        });

    [McpServerTool(Name = "get_log_directory")]
    [Description("Absolute path to the HotSonos log directory on this machine.")]
    public string GetLogDirectory() =>
        McpActivityLog.Run("get_log_directory", null, () => AppLog.DirectoryPath);

    // ---- Control (same actions as tray / hotkeys) -------------------------

    [McpServerTool(Name = "play_pause")]
    [Description("Toggle play/pause on the active Sonos group.")]
    public Task<string> PlayPause(CancellationToken ct) =>
        McpActivityLog.RunAsync("play_pause", null, () => RunActionAsync(HotsonosAction.PlayPause), category: "control");

    [McpServerTool(Name = "next_track")]
    [Description("Skip to next track on the active group.")]
    public Task<string> NextTrack(CancellationToken ct) =>
        McpActivityLog.RunAsync("next_track", null, () => RunActionAsync(HotsonosAction.Next), category: "control");

    [McpServerTool(Name = "previous_track")]
    [Description("Go to previous track on the active group.")]
    public Task<string> PreviousTrack(CancellationToken ct) =>
        McpActivityLog.RunAsync("previous_track", null, () => RunActionAsync(HotsonosAction.Previous), category: "control");

    [McpServerTool(Name = "volume_up")]
    [Description("Raise group volume by the configured step (cancels wake ramp if active).")]
    public Task<string> VolumeUp(CancellationToken ct) =>
        McpActivityLog.RunAsync("volume_up", null, () => RunActionAsync(HotsonosAction.VolumeUp), category: "control");

    [McpServerTool(Name = "volume_down")]
    [Description("Lower group volume by the configured step (cancels wake ramp if active).")]
    public Task<string> VolumeDown(CancellationToken ct) =>
        McpActivityLog.RunAsync("volume_down", null, () => RunActionAsync(HotsonosAction.VolumeDown), category: "control");

    [McpServerTool(Name = "mute_toggle")]
    [Description("Toggle mute on the active group (cancels wake ramp if active).")]
    public Task<string> MuteToggle(CancellationToken ct) =>
        McpActivityLog.RunAsync("mute_toggle", null, () => RunActionAsync(HotsonosAction.Mute), category: "control");

    [McpServerTool(Name = "level_volumes")]
    [Description("Set all speakers to the configured level volume percent and unmute (cancels wake ramp if active).")]
    public Task<string> LevelVolumes(CancellationToken ct) =>
        McpActivityLog.RunAsync("level_volumes", null, () => RunActionAsync(HotsonosAction.LevelVolumes), category: "control");

    [McpServerTool(Name = "shuffle_library")]
    [Description("Group all speakers under the active coordinator and client-side shuffle the full Music Library.")]
    public Task<string> ShuffleLibrary(CancellationToken ct) =>
        McpActivityLog.RunAsync("shuffle_library", null, () => RunActionAsync(HotsonosAction.ShuffleLibrary), category: "control");

    [McpServerTool(Name = "fresh_start")]
    [Description("Re-discover, regroup all speakers, and shuffle the library (Fresh Start).")]
    public Task<string> FreshStart(CancellationToken ct) =>
        McpActivityLog.RunAsync("fresh_start", null, () => RunActionAsync(HotsonosAction.FreshStart), category: "control");

    [McpServerTool(Name = "play_favorite_slot")]
    [Description("Play favorite/playlist hotkey slot 1-4 (must be assigned in Settings).")]
    public Task<string> PlayFavoriteSlot(
        [Description("Slot number 1 through 4")] int slot,
        CancellationToken ct) =>
        McpActivityLog.RunAsync("play_favorite_slot", new { slot }, () =>
        {
            var action = slot switch
            {
                1 => HotsonosAction.Favorite1,
                2 => HotsonosAction.Favorite2,
                3 => HotsonosAction.Favorite3,
                4 => HotsonosAction.Favorite4,
                _ => throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1-4."),
            };
            return RunActionAsync(action);
        }, category: "control");

    [McpServerTool(Name = "set_active_room")]
    [Description("Set the active target room/group by coordinator room name (same keys as list_groups.coordinatorRoom).")]
    public string SetActiveRoom(
        [Description("Coordinator room name, e.g. Office or Living Room")] string room) =>
        McpActivityLog.Run("set_active_room", new { room }, () =>
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
        }, category: "control");

    [McpServerTool(Name = "wake_now")]
    [Description("Start wake-to-music immediately using current Settings (skips if anything is already playing). Returns right away; ramp may run for many minutes. Use wake_cancel to stop the ramp.")]
    public string WakeNow() =>
        McpActivityLog.Run("wake_now", null, () =>
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
        }, category: "control");

    [McpServerTool(Name = "wake_cancel")]
    [Description("Cancel an in-progress wake volume ramp / expand (does not stop Sonos playback).")]
    public string WakeCancel() =>
        McpActivityLog.Run("wake_cancel", null, () =>
        {
            if (_state.Wake is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Wake service not available" }, JsonOptions);
            _state.Wake.Cancel();
            return JsonSerializer.Serialize(new { ok = true, message = "Wake cancel requested", wakeActive = _state.Wake.IsActive }, JsonOptions);
        }, category: "control");

    private async Task<string> RunActionAsync(HotsonosAction action)
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
