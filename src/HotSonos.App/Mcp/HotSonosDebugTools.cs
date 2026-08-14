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
                playback = sonos.GetPlaybackSessionSnapshot(),
                hint = groupCount == 0
                    ? "Device list empty — call refresh_devices, then list_groups / list_zones. Check get_logs if still empty."
                    : "Device list has groups; use list_groups or list_zones for details. Play a library track with play_library_track; return via resume_shuffle.",
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

    [McpServerTool(Name = "list_house_coordinator_candidates")]
    [Description(
        "Rooms that can be the preferred whole-house group coordinator. " +
        "Shows PreferredHouseCoordinatorRoom and who is leading now.")]
    public string ListHouseCoordinatorCandidates() =>
        McpActivityLog.Run("list_house_coordinator_candidates", null, () =>
        {
            var s = _state.Settings().EnsureShape();
            var candidates = _state.Sonos.GetCoordinatorCandidates();
            return JsonSerializer.Serialize(new
            {
                preferred = s.PreferredHouseCoordinatorRoom,
                activeRoom = _state.Sonos.ActiveRoom,
                candidates = candidates.Select(c => new
                {
                    room = c.Room,
                    ip = c.Ip,
                    uuid = c.Uuid,
                    isLeading = c.IsLeading,
                }),
            }, JsonOptions);
        });

    [McpServerTool(Name = "set_house_coordinator")]
    [Description(
        "Set preferred whole-house coordinator: BecomeCoordinator on that room, join every other player, " +
        "refresh topology, and VERIFY the room is actually leading. Fails if verification fails. " +
        "Persists PreferredHouseCoordinatorRoom. Shuffle/Fresh Start keep using it.")]
    public Task<string> SetHouseCoordinator(
        [Description("Room name exactly as list_zones / list_house_coordinator_candidates")] string room,
        CancellationToken ct) =>
        McpActivityLog.RunAsync("set_house_coordinator", new { room }, async () =>
        {
            try
            {
                var msg = await _state.Sonos.SetHouseCoordinatorAsync(room, ct).ConfigureAwait(false);
                var s = _state.Settings().EnsureShape();
                s.PreferredHouseCoordinatorRoom = room.Trim();
                s.ActiveRoom = room.Trim();
                _state.SetActiveRoom?.Invoke(room.Trim());
                _state.PersistSettings?.Invoke();
                var zones = _state.Sonos.GetCoordinatorCandidates();
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = msg,
                    preferred = s.PreferredHouseCoordinatorRoom,
                    groups = _state.Sonos.Groups.Select(g => new
                    {
                        g.DisplayName,
                        g.CoordinatorRoom,
                        g.CoordinatorIp,
                        g.MemberCount,
                    }),
                    candidates = zones.Select(c => new { c.Room, c.Ip, c.IsLeading }),
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = ex.Message,
                    preferred = _state.Settings().EnsureShape().PreferredHouseCoordinatorRoom,
                    groups = _state.Sonos.Groups.Select(g => new
                    {
                        g.DisplayName,
                        g.CoordinatorRoom,
                        g.MemberCount,
                    }),
                }, JsonOptions);
            }
        }, category: "control");

    [McpServerTool(Name = "regroup_house")]
    [Description(
        "Regroup all speakers under PreferredHouseCoordinatorRoom (or active room if unset). " +
        "Verifies topology afterward; returns ok=false if the house did not stick as one group.")]
    public Task<string> RegroupHouse(CancellationToken ct) =>
        McpActivityLog.RunAsync("regroup_house", null, async () =>
        {
            var s = _state.Settings().EnsureShape();
            var room = s.PreferredHouseCoordinatorRoom ?? _state.Sonos.ActiveRoom ?? s.ActiveRoom;
            if (string.IsNullOrWhiteSpace(room))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "No preferred or active room. Call set_house_coordinator first.",
                }, JsonOptions);
            }

            try
            {
                var msg = await _state.Sonos.SetHouseCoordinatorAsync(room, ct).ConfigureAwait(false);
                s.PreferredHouseCoordinatorRoom = room;
                s.ActiveRoom = room;
                _state.SetActiveRoom?.Invoke(room);
                _state.PersistSettings?.Invoke();
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = msg,
                    preferred = room,
                    groups = _state.Sonos.Groups.Select(g => new
                    {
                        g.DisplayName,
                        g.CoordinatorRoom,
                        g.CoordinatorIp,
                        g.MemberCount,
                    }),
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = ex.Message,
                    preferred = room,
                    groups = _state.Sonos.Groups.Select(g => new
                    {
                        g.DisplayName,
                        g.CoordinatorRoom,
                        g.MemberCount,
                    }),
                }, JsonOptions);
            }
        }, category: "control");

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
                preferredHouseCoordinatorRoom = s.PreferredHouseCoordinatorRoom,
                s.VolumeStep,
                s.LevelVolumePercent,
                roomVolumeOffsets = s.RoomVolumeOffsets
                    .Select(o => new { o.RoomName, o.OffsetPercent })
                    .ToList(),
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
                s.ContinueLibraryShuffleAfterSpecialPlay,
                s.ShuffleArtistSpread,
                s.ShowGenresInPlaySources,
                s.ControlShuffleSource,
                playHistoryDistinct = _state.Sonos.PlayHistory.PlayedDistinctCount,
                sonosLibraryRoots = s.SonosLibraryRoots,
                dailyLibraryRoots = s.GetEffectiveDailyLibraryRoots(),
                masterLibraryMappings = s.MasterLibraryMappings.Select(m => new
                {
                    sonosPath = m.SonosPath,
                    masterRoot = m.MasterRoot,
                }),
                // Legacy single field (first mapping) for older clients
                s.MasterLibraryRoot,
                favoriteSlots = s.FavoriteSlots.Select((f, i) => new
                {
                    slot = i + 1,
                    source = f.Source,
                    f.FavoriteName,
                    f.TagKey,
                    f.GenreName,
                    label = f.DisplayLabel(s),
                    hotkey = f.Hotkey.ToString(),
                }),
            }, JsonOptions);
        });

    [McpServerTool(Name = "get_library_config")]
    [Description("Configured Sonos library root path(s) and master mappings (Sonos path → hi-res master root).")]
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
                dailyLibraryRoots = s.GetEffectiveDailyLibraryRoots(),
                dailyShuffleScoped = s.GetDailyShuffleIncludePrefixes() is { Count: > 0 },
                masterLibraryMappings = s.MasterLibraryMappings.Select(m => new
                {
                    sonosPath = m.SonosPath,
                    masterRoot = m.MasterRoot,
                }),
                masterLibraryRoot = s.MasterLibraryRoot,
                configured = roots.Count > 0 || s.MasterLibraryMappings.Count > 0,
                trackCount = status?.TrackCount ?? 0,
                isScanning = status?.IsScanning ?? false,
                databasePath = status?.DatabasePath,
                note = "Master dual-write only for tracks under a mapped Sonos path. Unmapped folders (e.g. Christmas) are Sonos-only.",
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
    [Description("Search library cache. Default: title/artist/album/genre/tags/path. Prefixes (one only): T: title, A: artist, TG: tags (all must match), F: format (codec/extension). Max 200.")]
    public string LibrarySearch(
        [Description("Substring or T:/A:/TG:/F: restricted query; empty = browse")] string? query = null,
        [Description("Max rows (default 25, max 200)")] int limit = 25,
        [Description("Offset for paging")] int offset = 0,
        [Description("If true, only tracks flagged as outside Sonos local-library format limits")] bool sonosUnplayableOnly = false) =>
        McpActivityLog.Run("library_search", new { query, limit, offset, sonosUnplayableOnly }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            var s = _state.Settings().EnsureShape();
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
                    tagKeys = t.TagKeys,
                    tagsLabel = t.FormatTagLabels(k => s.TagLabel(k)),
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
                    t.MasterPath,
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

    [McpServerTool(Name = "track_find_master")]
    [Description("Preview master-library twin match for a Sonos-library track (no writes). Uses stored link, relative path, path suffix, then filename/metadata scoring.")]
    public string TrackFindMaster(
        [Description("Absolute/UNC path to the Sonos-library audio file")] string path) =>
        McpActivityLog.Run("track_find_master", new { path }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            var match = lib.FindMasterMatch(path);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                found = match.Found,
                kind = match.Kind.ToString(),
                masterPath = match.Path,
                message = match.Message,
                candidates = match.Candidates,
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "track_link_master")]
    [Description("Manually link (or clear) a master twin path for a cached Sonos track. Master path must be under the master root mapped for that Sonos path. Pass masterPath empty/null to clear.")]
    public string TrackLinkMaster(
        [Description("Absolute/UNC path to the Sonos-library audio file")] string path,
        [Description("Absolute/UNC path under the mapped master root, or empty to clear the link")] string? masterPath = null) =>
        McpActivityLog.Run("track_link_master", new { path, masterPath }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            var (ok, message, linked) = lib.LinkMaster(path, masterPath);
            return JsonSerializer.Serialize(new
            {
                ok,
                message,
                path,
                masterPath = linked,
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "list_tags")]
    [Description("List the flat tag catalog (opaque keys + renamable labels). Files store only keys in HOTSONOS_TAGS.")]
    public string ListTags() =>
        McpActivityLog.Run("list_tags", null, () =>
        {
            var s = _state.Settings().EnsureShape();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                updateMasterDefault = s.TagUpdateMasterDefault,
                tags = s.Tags.Select(t => new { t.Key, t.Label }),
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "tag_create")]
    [Description("Create a catalog tag with a fresh auto-generated key. Label is display-only.")]
    public string TagCreate(
        [Description("User-facing label")] string label) =>
        McpActivityLog.Run("tag_create", new { label }, () =>
        {
            var s = _state.Settings().EnsureShape();
            var tag = s.AddTag(label);
            if (tag is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Label is required." }, JsonOptions);
            try { _state.PersistSettings?.Invoke(); }
            catch { /* best effort */ }
            return JsonSerializer.Serialize(new { ok = true, tag = new { tag.Key, tag.Label } }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "tag_rename")]
    [Description("Rename a catalog tag label by key. Files keep the same key — no rewrite.")]
    public string TagRename(
        [Description("Opaque tag key")] string key,
        [Description("New display label")] string label) =>
        McpActivityLog.Run("tag_rename", new { key, label }, () =>
        {
            var s = _state.Settings().EnsureShape();
            if (!s.RenameTag(key, label))
                return JsonSerializer.Serialize(new { ok = false, error = "Unknown key or empty label." }, JsonOptions);
            try { _state.PersistSettings?.Invoke(); }
            catch { /* best effort */ }
            var t = s.FindTag(key);
            return JsonSerializer.Serialize(new { ok = true, tag = t is null ? null : new { t.Key, t.Label } }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "tag_delete")]
    [Description("Delete a catalog tag and strip its key from every library track that has it (writes HOTSONOS_TAGS).")]
    public string TagDelete(
        [Description("Opaque tag key from list_tags")] string key,
        [Description("Dual-write master when configured (default: settings)")] bool? updateMaster = null) =>
        McpActivityLog.Run("tag_delete", new { key, updateMaster }, () =>
        {
            var s = _state.Settings().EnsureShape();
            var def = s.FindTag(key);
            var label = def?.Label ?? key;
            TagPurgeResult? purge = null;
            var lib = _state.Library;
            if (lib is not null)
                purge = lib.PurgeTagKey(key, updateMaster);

            if (!s.RemoveTag(key) && def is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Unknown tag key.", purge }, JsonOptions);

            try { _state.PersistSettings?.Invoke(); }
            catch { /* best effort */ }

            return JsonSerializer.Serialize(new
            {
                ok = purge?.Ok != false,
                deleted = label,
                key,
                purge = purge is null
                    ? null
                    : new
                    {
                        purge.Matched,
                        purge.Written,
                        purge.Queued,
                        purge.Failed,
                        purge.Message,
                    },
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "track_toggle_tag")]
    [Description("Toggle or force one catalog tag on a track (writes HOTSONOS_TAGS keys). forceEnable null=toggle, true/false=on/off.")]
    public string TrackToggleTag(
        [Description("Absolute/UNC path (or Sonos URI) to the track")] string path,
        [Description("Opaque tag key from list_tags")] string key,
        [Description("null=toggle; true=force on; false=force off")] bool? forceEnable = null,
        [Description("If true, preview only")] bool dryRun = false,
        [Description("Dual-write master when configured")] bool? updateMaster = null) =>
        McpActivityLog.Run("track_toggle_tag", new { path, key, forceEnable, dryRun, updateMaster }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            var s = _state.Settings().EnsureShape();
            var result = lib.SetTagFlag(path, key, forceEnable, dryRun, updateMaster);
            return JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                dryRun = result.DryRun,
                path = result.Path,
                message = result.Message,
                error = result.Error,
                changes = result.Changes,
                queued = result.Queued,
                master = new
                {
                    path = result.MasterPath,
                    matchKind = result.MasterMatchKind,
                    message = result.MasterMessage,
                    error = result.MasterError,
                    written = result.MasterWritten,
                },
                track = result.TrackAfter is null ? null : new
                {
                    result.TrackAfter.Path,
                    result.TrackAfter.Title,
                    result.TrackAfter.Artist,
                    tagKeys = result.TrackAfter.TagKeys,
                    tagsLabel = result.TrackAfter.FormatTagLabels(k => s.TagLabel(k)),
                },
            }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "track_set_tags")]
    [Description("Replace HOTSONOS_TAGS key set and/or standard metadata fields on a Sonos-library track. tagKeys null=leave; empty array=clear all HotSonos tags.")]
    public string TrackSetTags(
        [Description("Absolute/UNC path to the audio file (same as library_search path)")] string path,
        [Description("Full replacement list of opaque tag keys (null = leave HOTSONOS_TAGS unchanged)")] string[]? tagKeys = null,
        [Description("Title (null = leave unchanged)")] string? title = null,
        [Description("Artist (null = leave unchanged)")] string? artist = null,
        [Description("Album (null = leave unchanged)")] string? album = null,
        [Description("Genre (null = leave unchanged)")] string? genre = null,
        [Description("Track number (null = leave unchanged)")] int? trackNumber = null,
        [Description("Year (null = leave unchanged)")] int? year = null,
        [Description("BPM (null = leave unchanged)")] double? bpm = null,
        [Description("If true, do not write the file — return planned changes only")] bool dryRun = false,
        [Description("If true (default), also write the same tags to a matched master twin when a master mapping covers this Sonos path")] bool updateMaster = true) =>
        McpActivityLog.Run("track_set_tags", new { path, tagKeys, title, artist, album, genre, trackNumber, year, bpm, dryRun, updateMaster }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library service not available." }, JsonOptions);

            var s = _state.Settings().EnsureShape();
            var update = new TrackTagUpdate
            {
                TagKeys = tagKeys,
                Title = title,
                Artist = artist,
                Album = album,
                Genre = genre,
                TrackNumber = trackNumber,
                Year = year,
                Bpm = bpm,
            };

            var result = lib.SetTags(path, update, dryRun, updateMaster);
            return JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                dryRun = result.DryRun,
                path = result.Path,
                message = result.Message,
                error = result.Error,
                changes = result.Changes,
                updateMaster = result.UpdateMasterRequested,
                master = new
                {
                    path = result.MasterPath,
                    matchKind = result.MasterMatchKind,
                    message = result.MasterMessage,
                    error = result.MasterError,
                    changes = result.MasterChanges,
                    written = result.MasterWritten,
                    candidates = result.MasterCandidates,
                },
                track = result.TrackAfter is null ? null : new
                {
                    result.TrackAfter.Path,
                    result.TrackAfter.Title,
                    result.TrackAfter.Artist,
                    result.TrackAfter.Album,
                    result.TrackAfter.Genre,
                    result.TrackAfter.TrackNumber,
                    result.TrackAfter.Year,
                    tagKeys = result.TrackAfter.TagKeys,
                    tagsLabel = result.TrackAfter.FormatTagLabels(k => s.TagLabel(k)),
                    result.TrackAfter.Bpm,
                    result.TrackAfter.MasterPath,
                    result.TrackAfter.AudioFormatLabel,
                    result.TrackAfter.SonosPlayable,
                    result.TrackAfter.SonosPlayIssue,
                },
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

    [McpServerTool(Name = "run_failure_diagnostic")]
    [Description(
        "Hard failure diagnostic: ping gateway/NAS/public DNS, probe library SMB roots, refresh Sonos topology, " +
        "ICMP+TCP:1400 every known zone, live now-playing, library cache, log tail. " +
        "Writes %LocalAppData%\\HotSonos\\diagnostics\\failure-*.txt. Run when audio cuts or network feels broken.")]
    public Task<string> RunFailureDiagnostic(CancellationToken ct) =>
        McpActivityLog.RunAsync("run_failure_diagnostic", null, async () =>
        {
            var diag = new FailureDiagnosticService(
                _state.Sonos,
                _state.Settings,
                _state.GetLastNowPlaying,
                _state.Library,
                () => _state.IsRunning,
                () => _state.Endpoint);
            var result = await diag.RunAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                ok = result.IssueCount == 0,
                issueCount = result.IssueCount,
                elapsedMs = result.ElapsedMs,
                reportPath = result.ReportPath,
                report = result.ReportText,
            }, JsonOptions);
        }, category: "debug");

    [McpServerTool(Name = "get_play_events")]
    [Description("Recent play lifecycle events (started, skipped, paused, resumed, previous, stopped). Also on disk at %LocalAppData%\\HotSonos\\play-events.jsonl and in app logs as 'Play started:' / 'Play skipped:' etc.")]
    public string GetPlayEvents(
        [Description("Max events to return (default 40, max 200)")] int max = 40,
        [Description("Optional kind filter: started | skipped | paused | resumed | previous | stopped")] string? kind = null) =>
        McpActivityLog.Run("get_play_events", new { max, kind }, () =>
        {
            max = Math.Clamp(max, 1, 200);
            var events = _state.Sonos.PlayEvents.GetRecent(max, kind);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                file = _state.Sonos.PlayEvents.FilePath,
                count = events.Count,
                events = events.Select(e => new
                {
                    utc = e.Utc,
                    e.Kind,
                    e.Display,
                    e.Title,
                    e.Artist,
                    e.Key,
                    e.Source,
                }),
            }, JsonOptions);
        });

    [McpServerTool(Name = "get_topology_events")]
    [Description("Recent speaker/Sub/Port topology events: group join/leave, vanished/returned, bonded Sub appear/disappear. File: %LocalAppData%\\HotSonos\\topology-events.jsonl. UI: Topology tab.")]
    public string GetTopologyEvents(
        [Description("Max events (default 80, max 300)")] int max = 80,
        [Description("Optional kind: baseline|groups_changed|vanished|returned|appeared|disappeared|bonded_appeared|bonded_disappeared|joined_group|left_group|moved_group")] string? kind = null) =>
        McpActivityLog.Run("get_topology_events", new { max, kind }, () =>
        {
            max = Math.Clamp(max, 1, 300);
            var events = _state.Sonos.TopologyEvents.GetRecent(max, kind);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                file = _state.Sonos.TopologyEvents.FilePath,
                count = events.Count,
                events = events.Select(e => new
                {
                    utc = e.Utc,
                    e.Kind,
                    e.Display,
                    e.RoomName,
                    e.Uuid,
                    e.IpAddress,
                    e.Invisible,
                    e.ChannelRole,
                    e.GroupCount,
                    e.Source,
                }),
            }, JsonOptions);
        });

    [McpServerTool(Name = "list_topology_members")]
    [Description("Current full topology including bonded/invisible devices (Sub, stereo pair mate). Use to see if Sub is present and which group Theater/Port is in.")]
    public string ListTopologyMembers() =>
        McpActivityLog.Run("list_topology_members", null, () =>
        {
            var snap = _state.Sonos.LastTopology;
            if (snap is null)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    message = "No topology snapshot yet — wait for GENA or call refresh_devices.",
                }, JsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                groupCount = snap.GroupCount,
                visibleCount = snap.VisibleCount,
                invisibleCount = snap.InvisibleCount,
                vanished = snap.VanishedRooms,
                subs = snap.Subs.Select(s => new
                {
                    s.RoomName,
                    s.Uuid,
                    s.IpAddress,
                    s.ChannelRole,
                    s.GroupId,
                    label = s.DisplayLabel,
                }),
                members = snap.Members.Select(m => new
                {
                    m.RoomName,
                    m.Uuid,
                    m.IpAddress,
                    m.Invisible,
                    m.ChannelRole,
                    m.GroupId,
                    m.CoordinatorUuid,
                    isCoordinator = m.IsCoordinator,
                    label = m.DisplayLabel,
                }),
            }, JsonOptions);
        });

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

    [McpServerTool(Name = "play_library_track")]
    [Description("Play one local library track on the active group (replaces the queue). Pass path from library_search. Use resume_shuffle afterward to return to library shuffle (fresh history-aware queue, not the exact prior order).")]
    public Task<string> PlayLibraryTrack(
        [Description("Absolute UNC path or x-file-cifs URI (library_search path)")] string path,
        [Description("Optional title for Sonos display")] string? title = null,
        [Description("Optional artist for Sonos display")] string? artist = null,
        CancellationToken ct = default) =>
        McpActivityLog.RunAsync("play_library_track", new { path, title, artist }, async () =>
        {
            if (_state.PlayLibraryTrackAsync is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Play library track not wired." }, JsonOptions);
            if (string.IsNullOrWhiteSpace(path))
                return JsonSerializer.Serialize(new { ok = false, error = "path is required" }, JsonOptions);

            try
            {
                var toast = await _state.PlayLibraryTrackAsync(path.Trim(), title, artist, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    toast,
                    path,
                    playback = _state.Sonos.GetPlaybackSessionSnapshot(),
                    activeRoom = _state.Sonos.ActiveRoom,
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLog.Error("MCP play_library_track failed", ex);
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message, path }, JsonOptions);
            }
        }, category: "control");

    [McpServerTool(Name = "resume_shuffle")]
    [Description("Return to Music Library shuffle after play_library_track (or anytime). Starts a fresh history-aware shuffle (not the exact previous queue). Groups all speakers like shuffle_library.")]
    public Task<string> ResumeShuffle(CancellationToken ct = default) =>
        McpActivityLog.RunAsync("resume_shuffle", null, async () =>
        {
            if (_state.ResumeShuffleAsync is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Resume shuffle not wired." }, JsonOptions);

            try
            {
                var toast = await _state.ResumeShuffleAsync(ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    toast,
                    playback = _state.Sonos.GetPlaybackSessionSnapshot(),
                    activeRoom = _state.Sonos.ActiveRoom,
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLog.Error("MCP resume_shuffle failed", ex);
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions);
            }
        }, category: "control");

    [McpServerTool(Name = "play_tag")]
    [Description("Play all library tracks with a catalog tag (e.g. Favs, Let's Rock), shuffled by default. Replaces the queue. When ContinueLibraryShuffleAfterSpecialPlay is true (default), auto top-up continues into full-library shuffle near the end of the tag queue — same as after play_library_track.")]
    public Task<string> PlayTag(
        [Description("Tag label or key from list_tags (e.g. Favs)")] string tag,
        [Description("Shuffle the tag queue (default true)")] bool shuffle = true,
        CancellationToken ct = default) =>
        McpActivityLog.RunAsync("play_tag", new { tag, shuffle }, async () =>
        {
            if (_state.PlayTaggedTracksAsync is null)
                return JsonSerializer.Serialize(new { ok = false, error = "play_tag not wired." }, JsonOptions);
            if (string.IsNullOrWhiteSpace(tag))
                return JsonSerializer.Serialize(new { ok = false, error = "tag is required" }, JsonOptions);

            try
            {
                var toast = await _state.PlayTaggedTracksAsync(tag.Trim(), shuffle, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    toast,
                    tag = tag.Trim(),
                    shuffle,
                    playback = _state.Sonos.GetPlaybackSessionSnapshot(),
                    activeRoom = _state.Sonos.ActiveRoom,
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLog.Error("MCP play_tag failed", ex);
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message, tag }, JsonOptions);
            }
        }, category: "control");

    [McpServerTool(Name = "list_genres")]
    [Description("List distinct Genre field values from the library cache with track counts (standard metadata, not HotSonos tags). Use before play_genre.")]
    public string ListGenres(
        [Description("Minimum track count to include (default 1)")] int minCount = 1) =>
        McpActivityLog.Run("list_genres", new { minCount }, () =>
        {
            var lib = _state.Library;
            if (lib is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Library not available." }, JsonOptions);

            var genres = lib.ListGenres(minCount);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                count = genres.Count,
                genres = genres.Select(g => new { genre = g.Genre, tracks = g.Count }),
            }, JsonOptions);
        });

    [McpServerTool(Name = "list_library_folders")]
    [Description("Configured Sonos library folders (from Discover) with cached track counts. Use path with play_folder.")]
    public string ListLibraryFolders() =>
        McpActivityLog.Run("list_library_folders", null, () =>
        {
            var s = _state.Settings().EnsureShape();
            var lib = _state.Library;
            var folders = s.SonosLibraryRoots.Select(path =>
            {
                var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
                return new
                {
                    path,
                    name = string.IsNullOrWhiteSpace(name) ? path : name,
                    tracks = lib?.CountTracksUnderFolder(path) ?? 0,
                    inDaily = s.GetEffectiveDailyLibraryRoots()
                        .Any(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase)),
                };
            }).ToList();
            return JsonSerializer.Serialize(new { ok = true, count = folders.Count, folders }, JsonOptions);
        }, category: "library");

    [McpServerTool(Name = "play_folder")]
    [Description("History-aware shuffle of one library folder (UNC path from list_library_folders / Discover). Top-up stays in that folder until Daily shuffle.")]
    public Task<string> PlayFolder(
        [Description("UNC folder path, e.g. \\\\192.168.1.111\\Music\\Jazz")] string path,
        [Description("Ignored; folder play always uses history-aware shuffle")] bool shuffle = true,
        CancellationToken ct = default) =>
        McpActivityLog.RunAsync("play_folder", new { path, shuffle }, async () =>
        {
            if (_state.PlayLibraryFolderAsync is null)
                return JsonSerializer.Serialize(new { ok = false, error = "play_folder not wired." }, JsonOptions);
            if (string.IsNullOrWhiteSpace(path))
                return JsonSerializer.Serialize(new { ok = false, error = "path is required" }, JsonOptions);

            try
            {
                var toast = await _state.PlayLibraryFolderAsync(path.Trim(), shuffle, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    toast,
                    path = path.Trim(),
                    playback = _state.Sonos.GetPlaybackSessionSnapshot(),
                    activeRoom = _state.Sonos.ActiveRoom,
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLog.Error("MCP play_folder failed", ex);
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message, path }, JsonOptions);
            }
        }, category: "control");

    [McpServerTool(Name = "play_genre")]
    [Description("Play all library tracks whose standard Genre field matches (case-insensitive label). Shuffled by default. Replaces the queue. When ContinueLibraryShuffleAfterSpecialPlay is true (default), auto top-up continues into full-library shuffle near the end — same as play_tag.")]
    public Task<string> PlayGenre(
        [Description("Genre label from list_genres (e.g. Rock, Jazz)")] string genre,
        [Description("Shuffle the genre queue (default true)")] bool shuffle = true,
        CancellationToken ct = default) =>
        McpActivityLog.RunAsync("play_genre", new { genre, shuffle }, async () =>
        {
            if (_state.PlayGenreTracksAsync is null)
                return JsonSerializer.Serialize(new { ok = false, error = "play_genre not wired." }, JsonOptions);
            if (string.IsNullOrWhiteSpace(genre))
                return JsonSerializer.Serialize(new { ok = false, error = "genre is required" }, JsonOptions);

            try
            {
                var toast = await _state.PlayGenreTracksAsync(genre.Trim(), shuffle, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    toast,
                    genre = genre.Trim(),
                    shuffle,
                    playback = _state.Sonos.GetPlaybackSessionSnapshot(),
                    activeRoom = _state.Sonos.ActiveRoom,
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLog.Error("MCP play_genre failed", ex);
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message, genre }, JsonOptions);
            }
        }, category: "control");

    [McpServerTool(Name = "fresh_start")]
    [Description("Re-discover, regroup all speakers, and shuffle the library (Fresh Start).")]
    public Task<string> FreshStart(CancellationToken ct) =>
        McpActivityLog.RunAsync("fresh_start", null, () => RunActionAsync(HotsonosAction.FreshStart), category: "control");

    [McpServerTool(Name = "play_favorite_slot")]
    [Description("Play favorite/playlist/folder/tag/genre hotkey slot 1-6 (must be assigned in Settings).")]
    public Task<string> PlayFavoriteSlot(
        [Description("Slot number 1 through 6")] int slot,
        CancellationToken ct) =>
        McpActivityLog.RunAsync("play_favorite_slot", new { slot }, () =>
        {
            var action = slot switch
            {
                1 => HotsonosAction.Favorite1,
                2 => HotsonosAction.Favorite2,
                3 => HotsonosAction.Favorite3,
                4 => HotsonosAction.Favorite4,
                5 => HotsonosAction.Favorite5,
                6 => HotsonosAction.Favorite6,
                _ => throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1-6."),
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
            np.TrackUri,
            np.IsEmpty,
            np.DisplayLine,
            sourceKind = np.SourceKind.ToString(),
            source = np.SourceLabel,
            sourceDetail = np.SourceDetail,
        };
    }
}
