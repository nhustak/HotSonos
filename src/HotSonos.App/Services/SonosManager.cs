using HotSonos.App.Infrastructure;
using HotSonos.App.Library;
using HotSonos.App.Models;
using HotSonos.Core;
using HotSonos.Core.Models;

namespace HotSonos.App.Services;

/// <summary>A selectable target: one Sonos group, named the way the Sonos app names it.</summary>
public sealed record SonosGroup(
    string DisplayName,
    string CoordinatorRoom,
    string CoordinatorUuid,
    string CoordinatorIp,
    int MemberCount)
{
    public override string ToString() => DisplayName;
}

/// <summary>A single speaker's current volume/mute, for the per-speaker settings list.</summary>
public sealed record SpeakerVolume(
    string RoomName,
    string IpAddress,
    int Volume,
    bool Muted,
    bool Reachable = true);

/// <summary>
/// Per-speaker EQ state. Bass/Treble are Sonos's −10…+10 steps.
/// <paramref name="Loudness"/> is null on products that do not expose it.
/// </summary>
public sealed record SpeakerEq(int Bass, int Treble, bool? Loudness);

/// <summary>
/// App-facing wrapper over the Core UPnP client. Holds the discovered topology
/// as groups and turns <see cref="HotsonosAction"/> into Sonos commands. Cheap
/// to call repeatedly; discovery is cached until refreshed.
/// </summary>
public sealed class SonosManager
{
    /// <summary>Rebuild older than this is treated as a leftover Sonos queue (overnight / post-restart).</summary>
    public static readonly TimeSpan StaleShuffleQueueAge = TimeSpan.FromHours(8);

    /// <summary>Min cooldown between automatic stale/replay reshuffles.</summary>
    private static readonly TimeSpan StaleReshuffleCooldown = TimeSpan.FromMinutes(12);

    private readonly SonosSoapClient _soap = new();
    private readonly SonosDiscovery _discovery;
    private readonly SonosEventSubscriber _events = new();
    private readonly PlayHistoryStore _playHistory;
    private readonly PlayEventLog _playEvents;
    private readonly TopologyEventLog _topologyEvents;
    private readonly ShuffleQueueStateStore _shuffleQueueState;
    private readonly Func<AppSettings> _settings;

    private IReadOnlyList<SonosZone> _zones = [];
    private SonosController? _controller;
    private SonosTopologySnapshot? _lastTopology;

    /// <summary>Cached product short names by player UUID (device_description).</summary>
    private readonly Dictionary<string, string> _productNameByUuid = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> _offline = [];
    private bool _topologySeen;
    private int _topUpInFlight; // 0/1
    private int _recoverInFlight; // 0/1
    private int _nowPlayingPollInFlight; // 0/1
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private System.Threading.Timer? _nowPlayingPollTimer;
    private DateTime _lastRecoverUtc = DateTime.MinValue;

    /// <summary>
    /// Sonos GENA TCP event subscriptions (AVTransport / topology). Default on — product path since Phase 2/3.
    /// </summary>
    public static bool UseGenaSubscriptions { get; set; } = true;

    /// <summary>
    /// SOAP poll of now-playing as backup/coalesce with GENA. Default on so UI stays live if GENA flaps.
    /// </summary>
    public static bool UseNowPlayingPoll { get; set; } = true;
    private int _recoverAttemptsInWindow;
    private DateTime _recoverWindowStartUtc = DateTime.MinValue;

    /// <summary>Drops redundant AVTransport GENA (Sonos can flood LastChange with identical payload).</summary>
    private string? _lastGenaNpSignature;

    /// <summary>Last track/state observed via GENA — drives start/pause/resume event logging.</summary>
    private string? _lastEventTrackKey;
    private string? _lastEventUri;
    private string? _lastEventTitle;
    private string? _lastEventArtist;
    private SonosTransportState _lastEventState = SonosTransportState.Unknown;
    private bool _skipNextStartLog; // set when we just logged "skipped" for this track change

    /// <summary>
    /// Normalized keys enqueued during this shuffle session (rebuild + top-ups).
    /// Top-up excludes these so a track already on the queue cannot be re-added
    /// before it has been heard/skipped (GENA only covers what actually started).
    /// </summary>
    private readonly HashSet<string> _sessionServedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _servedGate = new();

    /// <summary>Last exclusive library playback mode (for MCP / top-up policy).</summary>
    private string _playbackMode = "none"; // none | shuffle | folder | special (one-shot or tag queue)

    /// <summary>When <see cref="_playbackMode"/> is folder, top-up stays inside this path prefix.</summary>
    private string? _folderShufflePrefix;

    /// <summary>Last observed 1-based queue index (for detecting Play restarting the same batch).</summary>
    private int? _prevQueueTrackIndex;
    private int? _prevQueueTrackTotal;
    private DateTime _ignoreIndexResetUntilUtc = DateTime.MinValue;
    private DateTime _lastStaleReshuffleUtc = DateTime.MinValue;
    private int _staleReshuffleInFlight; // 0/1

    /// <summary>True after a library shuffle until a special play replaces the queue.</summary>
    public bool ShuffleSessionActive =>
        string.Equals(_playbackMode, "shuffle", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when shuffle or special play is active (resume_shuffle is meaningful).</summary>
    public bool CanResumeShuffle =>
        string.Equals(_playbackMode, "special", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_playbackMode, "one_shot", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_playbackMode, "shuffle", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_playbackMode, "folder", StringComparison.OrdinalIgnoreCase);

    /// <summary>Persisted last library shuffle rebuild (MCP / diagnostics).</summary>
    public ShuffleQueueStateStore ShuffleQueueState => _shuffleQueueState;

    public object GetPlaybackSessionSnapshot() => new
    {
        mode = _playbackMode,
        shuffleSessionActive = ShuffleSessionActive,
        canResumeShuffle = CanResumeShuffle,
        continueLibraryShuffleAfterSpecialPlay = _settings().EnsureShape().ContinueLibraryShuffleAfterSpecialPlay,
        folderPrefix = _folderShufflePrefix,
        shuffleQueue = _shuffleQueueState.Snapshot(),
        note = "shuffle = Daily mix; folder = one library path (top-up stays there); special = tag/genre/one-shot (top-up may enter Daily).",
    };

    /// <summary>Raised when the active coordinator pushes a now-playing change.</summary>
    public event Action<NowPlaying>? NowPlayingChanged;

    /// <summary>Raised when the speaker topology changes (regroup / drop / return).</summary>
    public event Action? TopologyChanged;

    /// <summary>
    /// Raised after house/per-speaker volume or mute writes, and when RenderingControl
    /// GENA reports a volume/mute change on the coordinator (UI should re-read sliders).
    /// </summary>
    public event Action? VolumesChanged;

    /// <summary>Raised when a speaker drops off (false) or comes back (true): (roomName, isOnline).</summary>
    public event Action<string, bool>? SpeakerAvailabilityChanged;

    /// <summary>Rooms currently reported as vanished/offline by Sonos.</summary>
    public IReadOnlyList<string> OfflineSpeakers => _offline;

    /// <summary>Number of visible zones in the last topology snapshot.</summary>
    public int GetZoneCount() => _zones.Count;

    /// <summary>Room + IP for every known zone (for failure diagnostics).</summary>
    public IReadOnlyList<(string Room, string Ip)> GetZoneEndpoints() =>
        _zones
            .Where(z => !string.IsNullOrWhiteSpace(z.IpAddress))
            .Select(z => (z.RoomName, z.IpAddress))
            .DistinctBy(z => z.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Diagnostic snapshot of cached topology (for MCP / Settings debug).</summary>
    public object GetTopologySnapshot() => new
    {
        deviceListPopulated = Groups.Count > 0,
        zoneCount = _zones.Count,
        groupCount = Groups.Count,
        activeRoom = ActiveRoom,
        offline = OfflineSpeakers,
        zones = _zones.Select(z => new
        {
            z.RoomName,
            z.IpAddress,
            z.Uuid,
            z.CoordinatorUuid,
            z.CoordinatorIpAddress,
            z.GroupId,
            z.IsCoordinator,
        }).ToList(),
        groups = Groups.Select(g => new
        {
            g.DisplayName,
            g.CoordinatorRoom,
            g.CoordinatorUuid,
            g.CoordinatorIp,
            g.MemberCount,
        }).ToList(),
    };

    public SonosManager(
        Func<AppSettings>? settings = null,
        PlayHistoryStore? playHistory = null,
        PlayEventLog? playEvents = null,
        TopologyEventLog? topologyEvents = null,
        ShuffleQueueStateStore? shuffleQueueState = null)
    {
        _settings = settings ?? AppSettings.CreateDefault;
        _playHistory = playHistory ?? new PlayHistoryStore(() => _settings().EnsureShape().ShuffleHistoryDays);
        _playEvents = playEvents ?? new PlayEventLog();
        _topologyEvents = topologyEvents ?? new TopologyEventLog();
        _shuffleQueueState = shuffleQueueState ?? new ShuffleQueueStateStore();
        _discovery = new SonosDiscovery(_soap);
        _events.NowPlayingChanged += HandleNowPlayingSnapshot;
        _events.TopologyChanged += OnTopologyEvent;
        _events.VolumeChanged += OnGenaVolumeChanged;

        if (UseGenaSubscriptions)
            AppLog.Info("GENA subscriptions ON (pre-isolation product path)");
        if (UseNowPlayingPoll)
            EnsureNowPlayingPoller();
        if (!UseGenaSubscriptions && !UseNowPlayingPoll)
            AppLog.Warn("GENA and now-playing poll both OFF — control-only (not recommended)");

        if (_shuffleQueueState.LastRebuildUtc is DateTime rebuilt)
        {
            AppLog.Info(
                $"Shuffle queue state: last rebuild {rebuilt:u} " +
                $"(age {(DateTime.UtcNow - rebuilt).TotalHours:0.0}h, size {_shuffleQueueState.LastRebuildQueueSize})");
        }
    }

    /// <summary>Latest now-playing snapshot (GENA or poll). Used to seed UI that opened mid-track.</summary>
    public NowPlaying? LastNowPlaying { get; private set; }

    /// <summary>Shared path for GENA or poll-based now-playing.</summary>
    private void HandleNowPlayingSnapshot(NowPlaying np)
    {
        try
        {
            // Always cache for Control/flyout seed even when signature is unchanged
            // (window can open mid-track and otherwise show "Nothing playing").
            LastNowPlaying = np;

            var sig = GenaNowPlayingSignature(np);
            if (string.Equals(sig, _lastGenaNpSignature, StringComparison.Ordinal))
                return;
            _lastGenaNpSignature = sig;

            var prevState = _lastEventState;
            try { ObservePlayLifecycle(np); }
            catch (Exception ex) { AppLog.Warn("ObservePlayLifecycle failed", ex); }

            try { MaybeRearmShuffleFromLibraryQueue(np); }
            catch (Exception ex) { AppLog.Warn("Rearm shuffle failed", ex); }

            try { MaybeDetectStaleOrReplayQueue(np); }
            catch (Exception ex) { AppLog.Warn("Stale/replay queue check failed", ex); }

            // History / top-up off the hot path — never block poll/UI on disk or library shuffle.
            var uri = np.TrackUri;
            var playing = np.State is SonosTransportState.Playing or SonosTransportState.Transitioning;
            if (playing && !string.IsNullOrWhiteSpace(uri))
            {
                _ = Task.Run(() =>
                {
                    try { _playHistory.RecordPlayed(uri); }
                    catch (Exception ex) { AppLog.Warn("RecordPlayed failed", ex); }
                });
            }

            try
            {
                var s = _settings().EnsureShape();
                if (s.ShuffleAutoTopUp
                    && ShouldAutoTopUp(s)
                    && playing
                    && np.IsNearQueueEnd(s.ShuffleTopUpWhenRemaining))
                {
                    _ = TryTopUpQueueAsync();
                }
            }
            catch (Exception ex) { AppLog.Warn("Top-up check failed", ex); }

            try { _ = MaybeRecoverPlaybackAsync(np, prevState); }
            catch (Exception ex) { AppLog.Warn("Recover schedule failed", ex); }

            try { NowPlayingChanged?.Invoke(np); }
            catch (Exception uiEx)
            {
                AppLog.Warn("NowPlayingChanged subscriber failed", uiEx);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Now-playing handler failed", ex);
        }
    }

    private void EnsureNowPlayingPoller()
    {
        if (_nowPlayingPollTimer is not null)
            return;

        AppLog.Info("Now-playing SOAP poll every 5s (backup / coalesce with GENA)");
        _nowPlayingPollTimer = new System.Threading.Timer(
            _ => _ = PollNowPlayingOnceAsync(),
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));
    }

    private async Task PollNowPlayingOnceAsync()
    {
        if (Interlocked.CompareExchange(ref _nowPlayingPollInFlight, 1, 0) != 0)
            return;

        try
        {
            if (_controller is null)
                return;

            var np = await _controller.GetNowPlayingSnapshotAsync().ConfigureAwait(false);
            HandleNowPlayingSnapshot(np);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Now-playing poll failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _nowPlayingPollInFlight, 0);
        }
    }

    /// <summary>
    /// One-shot SOAP now-playing (Quick Tag / MCP). Also feeds the poll/handler path so cache stays warm.
    /// </summary>
    public async Task<NowPlaying?> FetchNowPlayingAsync(CancellationToken ct = default)
    {
        if (_controller is null)
            return null;
        try
        {
            var np = await _controller.GetNowPlayingSnapshotAsync(ct).ConfigureAwait(false);
            HandleNowPlayingSnapshot(np);
            return np;
        }
        catch (Exception ex)
        {
            AppLog.Warn("FetchNowPlaying failed", ex);
            return null;
        }
    }

    public PlayHistoryStore PlayHistory => _playHistory;

    public PlayEventLog PlayEvents => _playEvents;

    /// <summary>Rooms / Sub / Port group join-leave and vanish trail.</summary>
    public TopologyEventLog TopologyEvents => _topologyEvents;

    /// <summary>Last full topology (includes bonded Sub / invisible members).</summary>
    public SonosTopologySnapshot? LastTopology => _lastTopology;

    /// <summary>
    /// Log start / pause / resume / stop from GENA now-playing changes.
    /// Skip is logged from the Next action before transport advances.
    /// </summary>
    private void ObservePlayLifecycle(NowPlaying np)
    {
        var key = PlayHistoryStore.NormalizeKey(np.TrackUri);
        var state = np.State;
        var title = np.Title;
        var artist = np.Artist;
        var uri = np.TrackUri;

        // Transport-only change (same track): pause / resume / stop.
        if (!string.IsNullOrEmpty(key)
            && string.Equals(key, _lastEventTrackKey, StringComparison.OrdinalIgnoreCase))
        {
            if (IsPausedLike(state) && IsPlayingLike(_lastEventState))
                _playEvents.Paused(uri, title, artist, "gena");
            else if (IsPlayingLike(state) && IsPausedLike(_lastEventState))
                _playEvents.Resumed(uri, title, artist, "gena");
            else if (state is SonosTransportState.Stopped
                     && _lastEventState is not SonosTransportState.Stopped
                     && _lastEventState is not SonosTransportState.Unknown)
                _playEvents.Stopped(uri, title, artist, "gena");

            _lastEventState = state;
            if (!string.IsNullOrWhiteSpace(title)) _lastEventTitle = title;
            if (!string.IsNullOrWhiteSpace(artist)) _lastEventArtist = artist;
            if (!string.IsNullOrWhiteSpace(uri)) _lastEventUri = uri;
            return;
        }

        // Track changed (or first observation).
        if (!string.IsNullOrEmpty(key)
            && IsPlayingLike(state)
            && !_skipNextStartLog)
        {
            _playEvents.Started(uri, title, artist, "gena");
        }

        _skipNextStartLog = false;
        _lastEventTrackKey = key.Length > 0 ? key : null;
        _lastEventUri = uri;
        _lastEventTitle = title;
        _lastEventArtist = artist;
        _lastEventState = state;
    }

    private static bool IsPlayingLike(SonosTransportState s) =>
        s is SonosTransportState.Playing or SonosTransportState.Transitioning;

    private static bool IsPausedLike(SonosTransportState s) =>
        s is SonosTransportState.PausedPlayback;

    /// <summary>
    /// After app restart, in-memory <see cref="_playbackMode"/> is "none" even while Sonos is
    /// still playing the library queue — top-up never runs and the same NORMAL queue can loop.
    /// If that queue is stale (rebuild older than <see cref="StaleShuffleQueueAge"/>), rebuild
    /// history-aware instead of only flipping the mode flag.
    /// </summary>
    private void MaybeRearmShuffleFromLibraryQueue(NowPlaying np)
    {
        if (!string.Equals(_playbackMode, "none", StringComparison.OrdinalIgnoreCase))
            return;
        if (!IsPlayingLike(np.State))
            return;
        if (!LooksLikeLibraryTrack(np.TrackUri))
            return;

        _playbackMode = "shuffle";
        AppLog.Info(
            $"Re-armed shuffle session from active library queue (mode was none; top-up enabled). " +
            $"track={np.CurrentTrack}/{np.NumberOfTracks} title={np.Title}");

        if (_shuffleQueueState.IsRebuildStale(StaleShuffleQueueAge))
        {
            var age = _shuffleQueueState.LastRebuildUtc is DateTime t
                ? $"{(DateTime.UtcNow - t).TotalHours:0.0}h"
                : "never";
            ScheduleStaleReshuffle(
                $"stale queue on rearm (last rebuild {age}, track={np.CurrentTrack}/{np.NumberOfTracks})");
        }
    }

    /// <summary>
    /// Detect Sonos restarting the same NORMAL queue (index jump backward) or a long-lived
    /// leftover batch after overnight / speaker reboot — then history-aware reshuffle.
    /// </summary>
    private void MaybeDetectStaleOrReplayQueue(NowPlaying np)
    {
        if (!IsPlayingLike(np.State) || !LooksLikeLibraryTrack(np.TrackUri))
            return;

        // Don't fight intentional non-library or special sessions.
        if (string.Equals(_playbackMode, "sonos_fav", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_playbackMode, "special", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_playbackMode, "one_shot", StringComparison.OrdinalIgnoreCase))
            return;

        var cur = np.CurrentTrack;
        var total = np.NumberOfTracks;
        _shuffleQueueState.ObservePosition(cur, total);

        // Index reset: e.g. 72/80 → 1/80 after Play recovery / coordinator reboot.
        var indexReset = false;
        var prevIdx = _prevQueueTrackIndex;
        if (cur is int c && c > 0 && prevIdx is int prev && prev > 0
            && DateTime.UtcNow >= _ignoreIndexResetUntilUtc)
        {
            if (prev >= 15 && c <= 5)
                indexReset = true;
            else if (prev - c >= 12 && c <= 10)
                indexReset = true;
        }

        _prevQueueTrackIndex = cur ?? _prevQueueTrackIndex;
        _prevQueueTrackTotal = total ?? _prevQueueTrackTotal;

        if (indexReset)
        {
            ScheduleStaleReshuffle(
                $"queue index reset ({prevIdx}→{cur}/{total} — likely Play/restart of same batch)");
            return;
        }

        // Already in shuffle/folder with a rebuild older than 8h (same queue riding for days).
        // Theater offline does not cause this — leftover coordinator queue does.
        if ((string.Equals(_playbackMode, "shuffle", StringComparison.OrdinalIgnoreCase)
             || string.Equals(_playbackMode, "folder", StringComparison.OrdinalIgnoreCase)
             || string.Equals(_playbackMode, "none", StringComparison.OrdinalIgnoreCase))
            && _shuffleQueueState.IsRebuildStale(StaleShuffleQueueAge))
        {
            var age = _shuffleQueueState.LastRebuildUtc is DateTime t
                ? $"{(DateTime.UtcNow - t).TotalHours:0.0}h"
                : "never";
            ScheduleStaleReshuffle(
                $"stale rebuild age {age} while mode={_playbackMode} track={cur}/{total}");
        }
    }

    /// <summary>
    /// Wake / MCP: if library is already playing but the last shuffle rebuild is stale,
    /// rebuild a history-aware queue. Returns a toast line, or null when no action taken.
    /// </summary>
    public async Task<string?> EnsureFreshLibraryShuffleIfStaleAsync(
        string reason,
        CancellationToken ct = default)
    {
        if (!_shuffleQueueState.IsRebuildStale(StaleShuffleQueueAge))
            return null;

        var np = LastNowPlaying;
        var library =
            np is not null && LooksLikeLibraryTrack(np.TrackUri)
            || string.Equals(_playbackMode, "shuffle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_playbackMode, "none", StringComparison.OrdinalIgnoreCase);

        if (!library)
            return null;

        var age = _shuffleQueueState.LastRebuildUtc is DateTime t
            ? $"{(DateTime.UtcNow - t).TotalHours:0.0}h"
            : "never";
        AppLog.Info($"Stale/replay queue detected ({reason}; last rebuild {age}) — reshuffling");
        var summary = await ShuffleWithHistoryAsync(ct).ConfigureAwait(false);
        return $"🔀 Stale queue ({reason}, rebuild age {age}) → fresh shuffle ({summary})";
    }

    /// <summary>True when last library shuffle rebuild is older than <see cref="StaleShuffleQueueAge"/>.</summary>
    public bool IsShuffleQueueStale() => _shuffleQueueState.IsRebuildStale(StaleShuffleQueueAge);

    private void ScheduleStaleReshuffle(string reason)
    {
        if ((DateTime.UtcNow - _lastStaleReshuffleUtc) < StaleReshuffleCooldown)
        {
            AppLog.Info($"Stale/replay reshuffle suppressed (cooldown): {reason}");
            return;
        }

        if (Interlocked.CompareExchange(ref _staleReshuffleInFlight, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Debounce GENA storms / double rearm / poll duplicates.
                await Task.Delay(1500).ConfigureAwait(false);
                if (_controller is null)
                    return;
                if ((DateTime.UtcNow - _lastStaleReshuffleUtc) < StaleReshuffleCooldown)
                    return;

                if (string.Equals(_playbackMode, "sonos_fav", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(_playbackMode, "special", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(_playbackMode, "one_shot", StringComparison.OrdinalIgnoreCase))
                    return;

                _lastStaleReshuffleUtc = DateTime.UtcNow;
                AppLog.Info($"Stale/replay queue detected ({reason}) — reshuffling");
                var summary = await ShuffleWithHistoryAsync().ConfigureAwait(false);
                AppLog.Info($"Stale/replay reshuffle OK ({summary})");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Stale/replay reshuffle failed ({reason})", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _staleReshuffleInFlight, 0);
            }
        });
    }

    private static bool LooksLikeLibraryTrack(string? trackUri)
    {
        if (string.IsNullOrWhiteSpace(trackUri))
            return false;
        return trackUri.Contains("x-file-cifs", StringComparison.OrdinalIgnoreCase)
               || trackUri.Contains("x-file-smb", StringComparison.OrdinalIgnoreCase)
               || trackUri.Contains("://", StringComparison.Ordinal)
                  && trackUri.Contains("/Music/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Identity for AVTransport GENA coalescing (ignore pure re-delivery of same state).
    /// </summary>
    private static string GenaNowPlayingSignature(NowPlaying np) =>
        string.Join('|',
            np.State.ToString(),
            np.TransportStatus ?? "",
            np.CurrentTrack?.ToString() ?? "",
            np.NumberOfTracks?.ToString() ?? "",
            np.TrackUri ?? "",
            np.Title ?? "");

    /// <summary>
    /// Recover from transport ERROR_* or unexpected STOPPED (after we were playing).
    /// Does not resume deliberate Pause. Rate-limited to avoid thrash loops.
    /// Never uses Play alone at end-of-queue — that restarts the same queue from track 1.
    /// Does <b>not</b> GroupAllSpeakers (that caused topology GENA storms / hard crashes).
    /// </summary>
    private async Task MaybeRecoverPlaybackAsync(NowPlaying np, SonosTransportState prevState)
    {
        var s = _settings().EnsureShape();
        if (!s.AutoRecoverPlayback)
            return;
        if (_controller is null)
            return;

        var status = np.TransportStatus ?? "";
        var hasError = status.Length > 0
                       && !status.Equals("OK", StringComparison.OrdinalIgnoreCase)
                       && status.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

        // User Pause must never auto-resume.
        if (np.State is SonosTransportState.PausedPlayback)
            return;

        var unexpectedStop = np.State is SonosTransportState.Stopped
                             && prevState is SonosTransportState.Playing
                                 or SonosTransportState.Transitioning;

        // Empty title+uri with ERROR is the classic ERROR_NO_RESOURCE blip mid-queue.
        if (!hasError && !unexpectedStop)
            return;

        // Ignore first snapshot after app start (Unknown → Stopped).
        if (prevState is SonosTransportState.Unknown)
            return;

        // Cooldown between recoveries.
        if ((DateTime.UtcNow - _lastRecoverUtc).TotalSeconds < 10)
            return;

        // Burst limit: at most 4 recovers per 15 minutes.
        if ((DateTime.UtcNow - _recoverWindowStartUtc).TotalMinutes > 15
            || _recoverWindowStartUtc == DateTime.MinValue)
        {
            _recoverWindowStartUtc = DateTime.UtcNow;
            _recoverAttemptsInWindow = 0;
        }

        if (_recoverAttemptsInWindow >= 4)
            return;

        if (Interlocked.CompareExchange(ref _recoverInFlight, 1, 0) != 0)
            return;

        var failedUri = np.TrackUri;
        var failedKey = PlayHistoryStore.NormalizeKey(failedUri);
        var atOrPastEnd = np.CurrentTrack is int cur
                          && np.NumberOfTracks is int total
                          && total > 0
                          && cur >= total;

        try
        {
            // Debounce GENA double-fires.
            await Task.Delay(1200).ConfigureAwait(false);
            if (_controller is null)
                return;

            var state = await _controller.GetTransportStateAsync().ConfigureAwait(false);
            if (IsPlayingLike(state))
                return; // already recovered on its own

            if (state is SonosTransportState.PausedPlayback)
                return; // user paused during debounce

            _recoverAttemptsInWindow++;
            _lastRecoverUtc = DateTime.UtcNow;

            AppLog.Info(
                $"Playback recovery start: prev={prevState} gena={np.State} status={status} " +
                $"track={np.CurrentTrack}/{np.NumberOfTracks} end={atOrPastEnd} " +
                $"title={np.Title} uri={np.TrackUri}");

            // Natural end of NORMAL queue (or STOPPED on last track) → fresh shuffle,
            // never Play (Play restarts the same order from track 1).
            // No GroupAll here — regroup storms crash the tray app.
            if (!hasError && (atOrPastEnd || np.IsNearQueueEnd(1)))
            {
                AppLog.Info("Playback recovery: end of queue — reshuffling (not Play)");
                var endSummary = await ShuffleWithHistoryAsync().ConfigureAwait(false);
                AppLog.Info($"Playback recovery: end-of-queue reshuffle OK ({endSummary})");
                return;
            }

            // 1) Bad resource / stuck stop mid-queue → skip to next item.
            try
            {
                await _controller.NextAsync().ConfigureAwait(false);
                AppLog.Info("Playback recovery: Next");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Playback recovery Next failed", ex);
            }

            await Task.Delay(600).ConfigureAwait(false);
            state = await _controller.GetTransportStateAsync().ConfigureAwait(false);
            if (IsPlayingLike(state))
            {
                // Guard: Next can no-op at end of queue and leave us on the same broken track after Play.
                var afterUri = await _controller.GetCurrentTrackUriAsync().ConfigureAwait(false);
                var afterKey = PlayHistoryStore.NormalizeKey(afterUri);
                if (hasError
                    && failedKey.Length > 0
                    && string.Equals(afterKey, failedKey, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Info("Playback recovery: still on failed track after Next — try Next again");
                    try { await _controller.NextAsync().ConfigureAwait(false); }
                    catch (Exception ex) { AppLog.Warn("Playback recovery second Next failed", ex); }
                    await Task.Delay(600).ConfigureAwait(false);
                    state = await _controller.GetTransportStateAsync().ConfigureAwait(false);
                    afterUri = await _controller.GetCurrentTrackUriAsync().ConfigureAwait(false);
                    afterKey = PlayHistoryStore.NormalizeKey(afterUri);
                    if (IsPlayingLike(state)
                        && string.Equals(afterKey, failedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Info("Playback recovery: same track stuck — reshuffling");
                        var stuckSummary = await ShuffleWithHistoryAsync().ConfigureAwait(false);
                        AppLog.Info($"Playback recovery: stuck-track reshuffle OK ({stuckSummary})");
                        return;
                    }
                }

                AppLog.Info($"Playback recovery: playing after Next ({state})");
                if (string.Equals(_playbackMode, "none", StringComparison.OrdinalIgnoreCase))
                    _playbackMode = "shuffle";
                return;
            }

            // 2) Still dead mid-queue → Play (resume at current index). Skip when we already
            // know we're at end — Play would restart the whole queue.
            if (atOrPastEnd)
            {
                AppLog.Info("Playback recovery: still stopped at end — reshuffling");
                var endSummary2 = await ShuffleWithHistoryAsync().ConfigureAwait(false);
                AppLog.Info($"Playback recovery: reshuffle OK ({endSummary2})");
                return;
            }

            try
            {
                await _controller.PlayAsync().ConfigureAwait(false);
                AppLog.Info("Playback recovery: Play");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Playback recovery Play failed", ex);
            }

            await Task.Delay(800).ConfigureAwait(false);
            state = await _controller.GetTransportStateAsync().ConfigureAwait(false);
            if (IsPlayingLike(state))
            {
                var afterUri = await _controller.GetCurrentTrackUriAsync().ConfigureAwait(false);
                var afterKey = PlayHistoryStore.NormalizeKey(afterUri);
                // Play after ERROR often restarts the same bad track — skip it.
                if (hasError
                    && failedKey.Length > 0
                    && string.Equals(afterKey, failedKey, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Info("Playback recovery: Play restarted failed track — Next");
                    try { await _controller.NextAsync().ConfigureAwait(false); }
                    catch (Exception ex) { AppLog.Warn("Playback recovery Next-after-Play failed", ex); }
                    await Task.Delay(600).ConfigureAwait(false);
                    state = await _controller.GetTransportStateAsync().ConfigureAwait(false);
                    afterUri = await _controller.GetCurrentTrackUriAsync().ConfigureAwait(false);
                    afterKey = PlayHistoryStore.NormalizeKey(afterUri);
                    if (IsPlayingLike(state)
                        && string.Equals(afterKey, failedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Info("Playback recovery: still failed track — reshuffling");
                        var sameSummary = await ShuffleWithHistoryAsync().ConfigureAwait(false);
                        AppLog.Info($"Playback recovery: reshuffle OK ({sameSummary})");
                        return;
                    }
                }

                AppLog.Info($"Playback recovery: playing after Play ({state})");
                if (string.Equals(_playbackMode, "none", StringComparison.OrdinalIgnoreCase))
                    _playbackMode = "shuffle";
                return;
            }

            // 3) Last resort: rebuild a history-aware library shuffle (keeps house music going).
            AppLog.Info("Playback recovery: reshuffling library (queue unrecoverable)");
            try
            {
                var summary = await ShuffleWithHistoryAsync().ConfigureAwait(false);
                AppLog.Info($"Playback recovery: reshuffle OK ({summary})");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Playback recovery reshuffle failed", ex);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Playback recovery failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _recoverInFlight, 0);
        }
    }

    private void OnTopologyEvent(string stateXml)
    {
        try
        {
            // Always: light path only (visible zones + vanished list). No full bonded parse / JSONL unless monitor on.
            var zones = SonosDiscovery.ParseZoneGroupState(stateXml);
            var groupsChanged = false;
            var controllerIpBefore = _controller?.CoordinatorIp;

            if (zones.Count > 0)
            {
                var prevGroupSig = GroupsSignature();
                _zones = zones;
                RebuildGroups();
                groupsChanged = !string.Equals(prevGroupSig, GroupsSignature(), StringComparison.Ordinal);
                // Re-subscribe only when the active coordinator IP actually changes.
                // GENA topology floods used to call RebuildController every time → SUBSCRIBE thrash.
                RebuildController(onlyIfCoordinatorChanged: true);
            }

            var vanishedNow = SonosDiscovery.ParseVanishedRooms(stateXml);

            // Heavy monitor: full member graph + event log (expensive on GENA floods).
            if (_settings().EnsureShape().TopologyMonitorEnabled)
            {
                var snap = SonosDiscovery.ParseTopologySnapshot(stateXml);
                _lastTopology = snap;
                _topologyEvents.Observe(snap, source: "gena");
                vanishedNow = snap.VanishedRooms;
            }

            var offlineChanged = false;
            // Skip drop/return balloons for the first snapshot — speakers already
            // offline at startup populate the indicator without a "just dropped" alert.
            if (_topologySeen)
            {
                var previous = new HashSet<string>(_offline, StringComparer.OrdinalIgnoreCase);
                var current = new HashSet<string>(vanishedNow, StringComparer.OrdinalIgnoreCase);
                foreach (var dropped in current.Where(r => !previous.Contains(r)))
                {
                    offlineChanged = true;
                    try { SpeakerAvailabilityChanged?.Invoke(dropped, false); }
                    catch (Exception ex) { AppLog.Warn($"SpeakerAvailabilityChanged(offline {dropped}) failed", ex); }
                }

                foreach (var returned in previous.Where(r => !current.Contains(r)))
                {
                    offlineChanged = true;
                    try { SpeakerAvailabilityChanged?.Invoke(returned, true); }
                    catch (Exception ex) { AppLog.Warn($"SpeakerAvailabilityChanged(online {returned}) failed", ex); }
                }
            }
            else
            {
                offlineChanged = vanishedNow.Count > 0 || _offline.Count > 0;
            }

            _offline = vanishedNow;
            _topologySeen = true;

            // Only notify UI when something meaningful changed — GENA often re-sends identical topology.
            var controllerIpAfter = _controller?.CoordinatorIp;
            var controllerChanged = !string.Equals(controllerIpBefore, controllerIpAfter, StringComparison.OrdinalIgnoreCase);
            if (groupsChanged || offlineChanged || controllerChanged)
            {
                try { TopologyChanged?.Invoke(); }
                catch (Exception ex) { AppLog.Warn("TopologyChanged subscriber failed", ex); }
            }
        }
        catch (Exception ex)
        {
            // A malformed topology push shouldn't disrupt anything.
            AppLog.Warn("Topology event parse failed", ex);
        }
    }

    private string GroupsSignature() =>
        string.Join("|", Groups.Select(g => $"{g.CoordinatorUuid}:{g.MemberCount}:{g.CoordinatorRoom}"));

    /// <summary>
    /// Pulls full topology (incl. Sub / bonded). Snapshot always updates on explicit
    /// refresh so Topology map works with Monitor OFF. Event-trail JSONL only when
    /// monitor is enabled (continuous GENA path already gates the same way).
    /// </summary>
    public async Task ObserveTopologyAsync(string source = "refresh", CancellationToken ct = default)
    {
        var monitor = _settings().EnsureShape().TopologyMonitorEnabled;
        // Explicit pull always allowed; passive sources stay light when monitor off.
        var explicitPull = string.Equals(source, "refresh", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(source, "ui", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase);
        if (!monitor && !explicitPull)
            return;

        var ip = _zones.FirstOrDefault()?.IpAddress
                 ?? Groups.FirstOrDefault()?.CoordinatorIp;
        if (string.IsNullOrWhiteSpace(ip))
            return;

        try
        {
            var snap = await _discovery.GetTopologySnapshotFromAsync(ip, ct).ConfigureAwait(false);
            if (snap.Members.Count == 0)
                return;
            _lastTopology = await EnrichTopologyProductNamesAsync(snap, ct).ConfigureAwait(false);
            if (monitor)
                _topologyEvents.Observe(_lastTopology, source);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Topology observe failed ({source})", ex);
        }
    }

    /// <summary>
    /// Fills <see cref="SonosTopologyMember.ProductName"/> from device description
    /// (cached by UUID). Used for topology type icons (Port / Era / One / Sub).
    /// </summary>
    private async Task<SonosTopologySnapshot> EnrichTopologyProductNamesAsync(
        SonosTopologySnapshot snap,
        CancellationToken ct)
    {
        var missing = snap.Members
            .Where(m => !string.IsNullOrWhiteSpace(m.IpAddress)
                        && !_productNameByUuid.ContainsKey(m.Uuid))
            .GroupBy(m => m.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (missing.Count > 0)
        {
            var probes = missing.Select(async m =>
            {
                var name = await SonosDeviceInfo.GetProductNameAsync(m.IpAddress, ct).ConfigureAwait(false);
                return (m.Uuid, name);
            });
            try
            {
                var results = await Task.WhenAll(probes).ConfigureAwait(false);
                foreach (var (uuid, name) in results)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        _productNameByUuid[uuid] = name!;
                }
            }
            catch
            {
                // Individual probes already swallow errors.
            }
        }

        if (_productNameByUuid.Count == 0)
            return snap;

        var enriched = snap.Members
            .Select(m =>
                _productNameByUuid.TryGetValue(m.Uuid, out var product)
                    ? m with { ProductName = product }
                    : m)
            .ToList();

        return new SonosTopologySnapshot
        {
            Members = enriched,
            VanishedRooms = snap.VanishedRooms,
        };
    }

    public ValueTask DisposeEventsAsync() => _events.DisposeAsync();

    /// <summary>Discovered groups, largest first (so "All Speakers" leads).</summary>
    public IReadOnlyList<SonosGroup> Groups { get; private set; } = [];

    /// <summary>Coordinator room name of the active group; the persisted target key.</summary>
    public string? ActiveRoom { get; private set; }

    /// <summary>
    /// Re-discovers the topology and (re)resolves the active group's controller.
    /// Serialized — concurrent App startup + MainWindow open used to race SUBSCRIBE/UNSUBSCRIBE
    /// and hard-kill the process without a managed exception.
    /// </summary>
    public async Task RefreshAsync(string? preferredRoom = null, CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _zones = await _discovery.DiscoverZonesAsync(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            RebuildGroups();

            var desired = preferredRoom ?? ActiveRoom;
            if (desired is null || !Groups.Any(g => ContainsRoom(g, desired)))
                desired = Groups.FirstOrDefault()?.CoordinatorRoom;

            ActiveRoom = desired;
            RebuildController();
            await ObserveTopologyAsync("refresh", ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Points subsequent commands at the group whose coordinator room is <paramref name="room"/>.</summary>
    public void SetActiveRoom(string room)
    {
        ActiveRoom = room;
        RebuildController();
    }

    /// <summary>Display name of the active group, for tray/menu labels.</summary>
    public string? ActiveGroupLabel =>
        Groups.FirstOrDefault(g => string.Equals(g.CoordinatorRoom, ActiveRoom, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? ActiveRoom;

    public async Task<IReadOnlyList<SonosFavorite>> GetFavoritesAsync(CancellationToken ct = default) =>
        _controller is null ? [] : await _controller.GetFavoritesAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Discovers Music Library filesystem roots from Sonos <c>A:TRACKS</c>
    /// (<c>x-file-cifs</c> URIs → UNC folders). Refreshes discovery if needed.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverMusicLibraryRootsAsync(CancellationToken ct = default)
    {
        if (_controller is null)
            await RefreshAsync(ActiveRoom, ct).ConfigureAwait(false);
        if (_controller is null)
            throw new InvalidOperationException("No Sonos speakers found. Refresh devices first.");

        return await _controller.DiscoverMusicLibraryRootsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an action and returns a short toast string (or null to show nothing).
    /// Throws on transport/network errors so the caller can surface them.
    /// </summary>
    public async Task<string?> ExecuteAsync(
        HotsonosAction action,
        AppSettings settings,
        CancellationToken ct = default,
        LibraryService? library = null)
    {
        // Fresh start re-discovers first (topology may have drifted, e.g. overnight),
        // so it must run before the "is a room selected" guard.
        if (action == HotsonosAction.FreshStart)
        {
            await RefreshAsync(ActiveRoom, ct).ConfigureAwait(false);
            if (_controller is null)
                throw new InvalidOperationException("No Sonos speakers found. Check the speakers are powered on and on the network.");
            await GroupAllSpeakersAsync(ct).ConfigureAwait(false);
            var fresh = await ShuffleWithHistoryAsync(ct).ConfigureAwait(false);
            _playbackMode = "shuffle";
            return $"🔄 Fresh start: re-synced + shuffle ({fresh})";
        }

        if (_controller is null)
            throw new InvalidOperationException("No Sonos room is selected. Open HotSonos and pick a room.");

        var slot = action.FavoriteSlotIndex();
        if (slot >= 0)
        {
            settings.EnsureShape();
            var fs = settings.FavoriteSlots[slot];
            if (fs.IsTag)
            {
                if (library is null)
                    throw new InvalidOperationException("Library service not available for tag play.");
                return await PlayTaggedTracksAsync(library, fs.TagKey!, shuffle: true, ct).ConfigureAwait(false);
            }

            if (fs.IsGenre)
            {
                if (library is null)
                    throw new InvalidOperationException("Library service not available for genre play.");
                return await PlayGenreTracksAsync(library, fs.GenreName!, shuffle: true, ct).ConfigureAwait(false);
            }

            if (fs.IsFolder)
            {
                if (library is null)
                    throw new InvalidOperationException("Library service not available for folder play.");
                return await PlayLibraryFolderAsync(library, fs.FolderPath!, shuffle: true, ct).ConfigureAwait(false);
            }

            if (!fs.IsSonos)
                return $"Slot {slot + 1} is empty — assign a folder, tag, genre, or Sonos playlist in Hotkeys.";

            await _controller.PlayFavoriteByNameAsync(fs.FavoriteName!, ct).ConfigureAwait(false);
            _playbackMode = "sonos_fav";
            return $"▶ {fs.FavoriteName}";
        }

        switch (action)
        {
            case HotsonosAction.PlayPause:
            {
                // Pause/resume lifecycle is logged from GENA (covers Sonos app too).
                var state = await _controller.PlayPauseAsync(ct).ConfigureAwait(false);
                return state == SonosTransportState.Playing ? "▶ Playing" : "⏸ Paused";
            }
            case HotsonosAction.Next:
            {
                // Skip = do not play again in the history window (same as finishing a track).
                await RecordCurrentTrackAsPlayedAsync(ct).ConfigureAwait(false);
                _playEvents.Skipped(_lastEventUri, _lastEventTitle, _lastEventArtist, "hotkey");
                _skipNextStartLog = false; // still log started for the next track via GENA
                await _controller.NextAsync(ct).ConfigureAwait(false);
                return "⏭ Next";
            }
            case HotsonosAction.Previous:
            {
                _playEvents.Previous(_lastEventUri, _lastEventTitle, _lastEventArtist, "hotkey");
                await _controller.PreviousAsync(ct).ConfigureAwait(false);
                return "⏮ Previous";
            }
            case HotsonosAction.ShuffleLibrary:
                await GroupAllSpeakersAsync(ct).ConfigureAwait(false);
                var shuffleSummary = await ShuffleWithHistoryAsync(ct).ConfigureAwait(false);
                _playbackMode = "shuffle";
                return $"🔀 Shuffling library → all speakers ({shuffleSummary})";
            case HotsonosAction.VolumeUp:
                return $"🔊 Volume {await ChangeVolumeAsync(settings.VolumeStep, ct).ConfigureAwait(false)}%";
            case HotsonosAction.VolumeDown:
                return $"🔊 Volume {await ChangeVolumeAsync(-settings.VolumeStep, ct).ConfigureAwait(false)}%";
            case HotsonosAction.Mute:
                return await ToggleMuteAsync(ct).ConfigureAwait(false) ? "🔇 Muted" : "🔊 Unmuted";
            case HotsonosAction.LevelVolumes:
                var n = await LevelAllVolumesAsync(settings.LevelVolumePercent, ct).ConfigureAwait(false);
                var offsets = settings.RoomVolumeOffsets?.Count(o => o.OffsetPercent != 0) ?? 0;
                return offsets > 0
                    ? $"🔉 Set {n} speaker(s) to {settings.LevelVolumePercent}% (with {offsets} room offset(s))"
                    : $"🔉 Set {n} speaker(s) to {settings.LevelVolumePercent}%";
            default:
                return null;
        }
    }

    // ---- Volume (house logical + per-room offsets) ------------------------
    // Group SetGroupVolume returns 803 with fixed-volume / Port members, so we
    // write per-player RenderingControl.
    //
    // Primary (toast / ± step base) = cached house logical, or a quick sample of
    // offset-0 rooms — never Port/Theater when it has a big offset.
    //
    // Snappy ± (old product feel): do NOT read all 10 speakers or await every
    // write before the toast. Cache logical, write all with short per-IP timeouts,
    // await only a reference room, fan the rest in the background.
    //
    // On write: raw = clamp(logical + roomOffset) so Port stays usable.

    private int _volumesChangedNotifyGate; // 0 = idle, 1 = flush scheduled
    private int _volumesChangedDirty;

    /// <summary>Last house logical % we set (Level all / ±). Avoids a full volume poll on every hotkey.</summary>
    private int? _houseLogicalVolume;

    /// <summary>
    /// Adjusts house <b>logical</b> volume by <paramref name="delta"/> and writes
    /// each speaker as logical + room offset. Returns the new logical % for toast
    /// as soon as a reference room accepts the write (others finish in background).
    /// </summary>
    private async Task<int> ChangeVolumeAsync(int delta, CancellationToken ct)
    {
        var s = _settings().EnsureShape();
        var zones = ZonesForVolumeControl();
        if (zones.Count == 0)
            return 0;

        var primary = _houseLogicalVolume
                      ?? await SampleHouseLogicalFastAsync(zones, s, ct).ConfigureAwait(false);
        var newLogical = Math.Clamp(primary + delta, 0, 100);
        _houseLogicalVolume = newLogical;

        // Prefer a normal (offset-0) room for the awaited "I heard you" write.
        var reference = zones.FirstOrDefault(z => s.GetVolumeOffset(z.RoomName) == 0)
                        ?? zones[0];

        // Background: everyone else (short timeout each — never block toast on Kitchen/etc.).
        foreach (var z in zones)
        {
            if (string.Equals(z.IpAddress, reference.IpAddress, StringComparison.OrdinalIgnoreCase))
                continue;
            var ip = z.IpAddress;
            var raw = s.ApplyVolumeOffset(z.RoomName, newLogical);
            _ = Task.Run(async () =>
            {
                using var bCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(450));
                await SetMemberVolumeOnlyAsync(ip, raw, bCts.Token).ConfigureAwait(false);
            });
        }

        // Foreground: one fast write so the user feels the change immediately.
        using (var fCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            fCts.CancelAfter(TimeSpan.FromMilliseconds(450));
            var refRaw = s.ApplyVolumeOffset(reference.RoomName, newLogical);
            await SetMemberVolumeOnlyAsync(reference.IpAddress, refRaw, fCts.Token).ConfigureAwait(false);
        }

        // Debounced Speakers-list refresh — after toast path returns.
        NotifyVolumesChanged();
        return newLogical;
    }

    /// <summary>
    /// Quick sample of offset-0 rooms only (≤350ms). Used once until Level all / ±
    /// establish <see cref="_houseLogicalVolume"/>.
    /// </summary>
    private async Task<int> SampleHouseLogicalFastAsync(
        IReadOnlyList<SonosZone> zones,
        AppSettings s,
        CancellationToken ct)
    {
        var candidates = zones
            .Where(z => s.GetVolumeOffset(z.RoomName) == 0)
            .Take(4)
            .ToList();
        if (candidates.Count == 0)
            candidates = zones.Take(3).ToList();

        using var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sampleCts.CancelAfter(TimeSpan.FromMilliseconds(350));

        var tasks = candidates
            .Select(z => GetVolumeOnlyAsync(z.IpAddress, sampleCts.Token)
                .ContinueWith(
                    t => (ok: t.Status == TaskStatus.RanToCompletion && t.Result is >= 0,
                        vol: t.Status == TaskStatus.RanToCompletion ? t.Result : -1,
                        room: z.RoomName),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Individual failures handled below.
        }

        var vols = new List<int>();
        foreach (var t in tasks)
        {
            if (!t.IsCompletedSuccessfully) continue;
            var r = t.Result;
            if (r.ok && r.vol is >= 0 and <= 100)
            {
                var off = s.GetVolumeOffset(r.room);
                vols.Add(Math.Clamp(r.vol - off, 0, 100));
            }
        }

        if (vols.Count > 0)
            return MedianInt(vols);
        return 20; // safe default if nothing answered (matches typical Level all)
    }

    /// <summary>GetVolume only (no mute) — volume ± sampling.</summary>
    private async Task<int> GetVolumeOnlyAsync(string ip, CancellationToken ct)
    {
        try
        {
            var r = await _soap.InvokeAsync(
                ip, SonosService.RenderingControl, "GetVolume",
                [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
            return int.TryParse(SonosSoapClient.ReadValue(r, "CurrentVolume"), out var v) ? v : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Public volume step for coalesced hotkeys. Prefer one call with a combined delta
    /// over many stacked VolumeUp actions (which walked volume to 100% during lag).
    /// Returns house logical % (not Port/coordinator raw).
    /// </summary>
    public Task<int> AdjustVolumeByAsync(int delta, CancellationToken ct = default) =>
        ChangeVolumeAsync(delta, ct);

    /// <summary>Visible zones in the active group (else all rooms), one per IP.</summary>
    private List<SonosZone> ZonesForVolumeControl()
    {
        IEnumerable<SonosZone> q = _zones.Where(z => !string.IsNullOrWhiteSpace(z.IpAddress));
        if (_controller is not null)
        {
            var uuid = _controller.CoordinatorUuid;
            var inGroup = q.Where(z =>
                string.Equals(z.CoordinatorUuid, uuid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inGroup.Count > 0)
                q = inGroup;
        }

        return q
            .GroupBy(z => z.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// House logical level: median raw % of rooms with offset 0. If every room has
    /// an offset, median of (raw − offset) estimates logical.
    /// </summary>
    internal static int ComputeHouseLogicalVolume(IReadOnlyList<SpeakerVolume> volumes, AppSettings settings)
    {
        if (volumes is null || volumes.Count == 0)
            return 0;

        var zeroOffset = new List<int>();
        var stripped = new List<int>();
        foreach (var v in volumes)
        {
            if (!v.Reachable)
                continue;
            var off = settings.GetVolumeOffset(v.RoomName);
            stripped.Add(Math.Clamp(v.Volume - off, 0, 100));
            if (off == 0)
                zeroOffset.Add(v.Volume);
        }

        if (zeroOffset.Count > 0)
            return MedianInt(zeroOffset);
        if (stripped.Count > 0)
            return MedianInt(stripped);
        return 0;
    }

    private static int MedianInt(List<int> values)
    {
        values.Sort();
        var n = values.Count;
        if (n == 0)
            return 0;
        var mid = n / 2;
        return n % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) / 2;
    }

    private void OnGenaVolumeChanged() => NotifyVolumesChanged();

    private void NotifyVolumesChanged()
    {
        // Coalesce GENA/write bursts so the UI does one refresh, never drop the last one.
        Volatile.Write(ref _volumesChangedDirty, 1);
        if (Interlocked.CompareExchange(ref _volumesChangedNotifyGate, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref _volumesChangedDirty, 0) == 0)
                        break;
                    try
                    {
                        VolumesChanged?.Invoke();
                    }
                    catch
                    {
                        // UI subscribers must not tear down volume path.
                    }
                }
            }
            catch
            {
                // ignore delay cancel
            }
            finally
            {
                Interlocked.Exchange(ref _volumesChangedNotifyGate, 0);
                if (Volatile.Read(ref _volumesChangedDirty) == 1)
                    NotifyVolumesChanged();
            }
        });
    }

    /// <summary>
    /// Sets EVERY visible speaker (across all groups) to the absolute volume
    /// (plus any per-room offset) and unmutes them. Returns the count of speakers
    /// that accepted the change (fixed-volume members are not counted).
    /// </summary>
    public async Task<int> LevelAllVolumesAsync(int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        _houseLogicalVolume = percent;
        var s = _settings().EnsureShape();
        var byIp = _zones
            .GroupBy(z => z.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var results = await Task.WhenAll(byIp.Select(z =>
        {
            var actual = s.ApplyVolumeOffset(z.RoomName, percent);
            return SetMemberVolumeAsync(z.IpAddress, actual, ct);
        })).ConfigureAwait(false);
        NotifyVolumesChanged();
        return results.Count(ok => ok);
    }

    /// <returns>True when the speaker accepted the volume (and unmute) change.</returns>
    private async Task<bool> SetMemberVolumeAsync(string ip, int percent, CancellationToken ct)
    {
        try
        {
            await SetMemberVolumeOnlyAsync(ip, percent, ct).ConfigureAwait(false);
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetMute",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredMute", "0")], ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Fixed-volume members (Sub/Port/Amp line-out) reject volume changes; ignore them.
            return false;
        }
    }

    /// <summary>SetVolume only — hotkey ± must stay one SOAP call per speaker (no unmute).</summary>
    private async Task SetMemberVolumeOnlyAsync(string ip, int percent, CancellationToken ct)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetVolume",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredVolume", percent.ToString())],
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Offline / fixed-volume / timeout — ignore for snappy ±.
        }
    }

    /// <summary>Reads every visible speaker's current volume/mute, for the settings-window list.</summary>
    public async Task<IReadOnlyList<SpeakerVolume>> GetSpeakerVolumesAsync(CancellationToken ct = default) =>
        await Task.WhenAll(_zones
                .DistinctBy(z => z.IpAddress, StringComparer.OrdinalIgnoreCase)
                .OrderBy(z => z.RoomName, StringComparer.OrdinalIgnoreCase)
                .Select(z => GetSpeakerVolumeAsync(z.RoomName, z.IpAddress, ct)))
            .ConfigureAwait(false);

    private async Task<SpeakerVolume> GetSpeakerVolumeAsync(string roomName, string ip, CancellationToken ct)
    {
        try
        {
            var volumeResponse = await _soap.InvokeAsync(ip, SonosService.RenderingControl, "GetVolume",
                [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
            var muteResponse = await _soap.InvokeAsync(ip, SonosService.RenderingControl, "GetMute",
                [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
            var volume = int.TryParse(SonosSoapClient.ReadValue(volumeResponse, "CurrentVolume"), out var v) ? v : 0;
            var muted = SonosSoapClient.ReadValue(muteResponse, "CurrentMute") == "1";
            return new SpeakerVolume(roomName, ip, volume, muted);
        }
        catch
        {
            return new SpeakerVolume(roomName, ip, 0, false, Reachable: false);
        }
    }

    /// <summary>Sets one speaker's absolute volume (leaves its mute state untouched).</summary>
    public async Task SetSpeakerVolumeAsync(string ip, int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetVolume",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredVolume", percent.ToString())], ct).ConfigureAwait(false);
            NotifyVolumesChanged();
        }
        catch
        {
            // Fixed-volume members (Sub/Port/Amp line-out) reject volume changes; ignore them.
        }
    }

    /// <summary>
    /// Per-speaker EQ read: bass, treble, loudness. Sonos ranges are −10…+10 for
    /// bass/treble. Returns null when the speaker does not answer or the product has
    /// no EQ (Sub-only bonds, some line-out devices).
    /// </summary>
    public async Task<SpeakerEq?> GetSpeakerEqAsync(string ip, CancellationToken ct = default)
    {
        try
        {
            var bassResponse = await _soap.InvokeAsync(ip, SonosService.RenderingControl, "GetBass",
                [new("InstanceID", "0")], ct).ConfigureAwait(false);
            var trebleResponse = await _soap.InvokeAsync(ip, SonosService.RenderingControl, "GetTreble",
                [new("InstanceID", "0")], ct).ConfigureAwait(false);

            var bass = int.TryParse(SonosSoapClient.ReadValue(bassResponse, "CurrentBass"), out var b) ? b : 0;
            var treble = int.TryParse(SonosSoapClient.ReadValue(trebleResponse, "CurrentTreble"), out var t) ? t : 0;

            // Loudness is optional on some products — never fail the whole read for it.
            bool? loudness = null;
            try
            {
                var loudResponse = await _soap.InvokeAsync(ip, SonosService.RenderingControl, "GetLoudness",
                    [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
                loudness = SonosSoapClient.ReadValue(loudResponse, "CurrentLoudness") == "1";
            }
            catch { /* product without loudness */ }

            return new SpeakerEq(bass, treble, loudness);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sets one speaker's bass (−10…+10).</summary>
    public Task SetSpeakerBassAsync(string ip, int value, CancellationToken ct = default) =>
        SetEqValueAsync(ip, "SetBass", "DesiredBass", Math.Clamp(value, -10, 10), ct);

    /// <summary>Sets one speaker's treble (−10…+10).</summary>
    public Task SetSpeakerTrebleAsync(string ip, int value, CancellationToken ct = default) =>
        SetEqValueAsync(ip, "SetTreble", "DesiredTreble", Math.Clamp(value, -10, 10), ct);

    private async Task SetEqValueAsync(string ip, string action, string argName, int value, CancellationToken ct)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, action,
                [new("InstanceID", "0"), new(argName, value.ToString())], ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Sub / fixed-output devices reject EQ writes; surface once, do not throw.
            AppLog.Warn($"{action} failed on {ip}", ex);
        }
    }

    /// <summary>Turns loudness compensation on/off for one speaker.</summary>
    public async Task SetSpeakerLoudnessAsync(string ip, bool on, CancellationToken ct = default)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetLoudness",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredLoudness", on ? "1" : "0")], ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"SetLoudness failed on {ip}", ex);
        }
    }

    /// <summary>Mutes/unmutes one speaker.</summary>
    public async Task SetSpeakerMuteAsync(string ip, bool mute, CancellationToken ct = default)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetMute",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredMute", mute ? "1" : "0")], ct).ConfigureAwait(false);
            NotifyVolumesChanged();
        }
        catch
        {
            // Tolerate members that reject mute.
        }
    }

    /// <summary>Toggles mute across the group; returns the new muted state.</summary>
    private async Task<bool> ToggleMuteAsync(CancellationToken ct)
    {
        var desired = !await GetGroupMuteAsync(ct).ConfigureAwait(false);
        var members = ActiveGroupMemberIps();
        await Task.WhenAll(members.Select(ip => SetMemberMuteAsync(ip, desired, ct))).ConfigureAwait(false);
        NotifyVolumesChanged();
        return desired;
    }

    private async Task SetMemberMuteAsync(string ip, bool mute, CancellationToken ct)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetMute",
                [
                    new("InstanceID", "0"),
                    new("Channel", "Master"),
                    new("DesiredMute", mute ? "1" : "0"),
                ], ct).ConfigureAwait(false);
        }
        catch
        {
            // Tolerate members that reject mute.
        }
    }

    private async Task<int> GetGroupVolumeAsync(CancellationToken ct)
    {
        try
        {
            var r = await _soap.InvokeAsync(_controller!.CoordinatorIp, SonosService.GroupRenderingControl,
                "GetGroupVolume", [new("InstanceID", "0")], ct).ConfigureAwait(false);
            return int.TryParse(SonosSoapClient.ReadValue(r, "CurrentVolume"), out var v) ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<bool> GetGroupMuteAsync(CancellationToken ct)
    {
        // Read the coordinator's per-player mute (RenderingControl), not the group
        // mute flag — we set mute per-player (SetGroupMute 803s on this system), so
        // the group flag never changes and would make the toggle one-way.
        try
        {
            var r = await _soap.InvokeAsync(_controller!.CoordinatorIp, SonosService.RenderingControl,
                "GetMute", [new("InstanceID", "0"), new("Channel", "Master")], ct).ConfigureAwait(false);
            return SonosSoapClient.ReadValue(r, "CurrentMute") == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>IPs of the visible members in the active group.</summary>
    private IReadOnlyList<string> ActiveGroupMemberIps()
    {
        if (_controller is null)
            return [];
        return MemberIpsForCoordinator(_controller.CoordinatorUuid);
    }

    /// <summary>Resolves a group by coordinator room name or any member room name.</summary>
    public SonosGroup? TryGetGroup(string? room)
    {
        if (string.IsNullOrWhiteSpace(room))
            return null;
        return Groups.FirstOrDefault(g => string.Equals(g.CoordinatorRoom, room, StringComparison.OrdinalIgnoreCase))
            ?? Groups.FirstOrDefault(g => ContainsRoom(g, room));
    }

    /// <summary>Builds a controller for a room/group without changing <see cref="ActiveRoom"/>.</summary>
    public SonosController? CreateControllerForRoom(string? room)
    {
        var group = TryGetGroup(room);
        if (group is not null)
            return new SonosController(group.CoordinatorIp, group.CoordinatorUuid, _soap);

        var zone = _zones.FirstOrDefault(z => string.Equals(z.RoomName, room, StringComparison.OrdinalIgnoreCase));
        return zone is null ? null : SonosController.ForZone(zone, _soap);
    }

    /// <summary>Visible member IPs for the group coordinated by <paramref name="coordinatorUuid"/>.</summary>
    public IReadOnlyList<string> MemberIpsForCoordinator(string coordinatorUuid) =>
        _zones
            .Where(z => string.Equals(z.CoordinatorUuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase))
            .Select(z => z.IpAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>All visible speaker IPs (any group).</summary>
    public IReadOnlyList<string> AllVisibleIps() =>
        _zones.Select(z => z.IpAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Sets absolute volume and unmutes every IP in <paramref name="ips"/>.
    /// Applies per-room offsets from settings (same as Level all) so wake ramps
    /// and house levels stay calibrated for amp-fed ports.
    /// </summary>
    public async Task SetVolumesAbsoluteAsync(IReadOnlyList<string> ips, int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        _houseLogicalVolume = percent;
        var s = _settings().EnsureShape();
        await Task.WhenAll(ips.Select(ip =>
        {
            var room = _zones.FirstOrDefault(z =>
                string.Equals(z.IpAddress, ip, StringComparison.OrdinalIgnoreCase))?.RoomName;
            var actual = s.ApplyVolumeOffset(room, percent);
            return SetMemberVolumeAsync(ip, actual, ct);
        })).ConfigureAwait(false);
        NotifyVolumesChanged();
    }

    /// <summary>
    /// Visible rooms that can lead a house group (one entry per room name).
    /// <paramref name="IsLeading"/> = currently coordinator of at least one group.
    /// </summary>
    public IReadOnlyList<(string Room, string Ip, string Uuid, bool IsLeading)> GetCoordinatorCandidates()
    {
        return _zones
            .Where(z => !string.IsNullOrWhiteSpace(z.RoomName) && !string.IsNullOrWhiteSpace(z.Uuid))
            .GroupBy(z => z.RoomName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // Prefer a zone entry that is already a coordinator if the room has multiple.
                var z = g.FirstOrDefault(x => x.IsCoordinator) ?? g.First();
                return (z.RoomName, z.IpAddress, z.Uuid, z.IsCoordinator);
            })
            .OrderBy(c => c.RoomName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Pulls every visible player under one coordinator so playback covers all speakers.
    /// Uses <see cref="AppSettings.PreferredHouseCoordinatorRoom"/> when that room is online;
    /// otherwise the active group's coordinator. Always re-discovers and verifies topology.
    /// </summary>
    public async Task GroupAllSpeakersAsync(CancellationToken ct = default)
    {
        var preferred = _settings().EnsureShape().PreferredHouseCoordinatorRoom;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            try
            {
                await SetHouseCoordinatorAsync(preferred, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn(
                    $"GroupAll via preferred '{preferred}' failed — falling back to active coordinator",
                    ex);
            }
        }

        if (_zones.Count == 0)
            await RefreshAsync(ActiveRoom, ct).ConfigureAwait(false);
        if (_controller is null)
            return;

        var uuid = _controller.CoordinatorUuid;
        var room = ActiveRoom ?? _controller.CoordinatorIp;
        await RegroupAllToUuidAsync(uuid, room, ct).ConfigureAwait(false);
        await RefreshAsync(ActiveRoom, ct).ConfigureAwait(false);
        LogGroupingVerification(uuid, room);
    }

    /// <summary>
    /// Make <paramref name="roomName"/> the whole-house group coordinator, join every other
    /// room to it, re-discover, and <b>verify</b> topology. Throws if verification fails.
    /// </summary>
    public async Task<string> SetHouseCoordinatorAsync(string roomName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            throw new ArgumentException("Room name required.", nameof(roomName));

        await RefreshAsync(null, ct).ConfigureAwait(false);

        var allow = _settings().EnsureShape().GetDailyGroupRoomAllowList();
        var want = roomName.Trim();
        // Preferred must be in the Daily speaker set when subset mode is on.
        if (allow is not null && !allow.Contains(want))
        {
            var fallback = allow
                .Select(r => _zones.FirstOrDefault(z =>
                    string.Equals(z.RoomName, r, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(z => z is not null);
            if (fallback is null)
                throw new InvalidOperationException(
                    $"Preferred coordinator '{want}' is not in the Daily speaker list, " +
                    "and none of the checked rooms are online. Check Control → Speakers in Daily.");
            AppLog.Warn(
                $"Preferred coordinator '{want}' not in Daily list — using '{fallback.RoomName}' instead");
            want = fallback.RoomName;
        }

        var zone = _zones.FirstOrDefault(z =>
            string.Equals(z.RoomName, want, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
            throw new InvalidOperationException(
                $"Room '{want}' not found. Refresh devices and pick a room from the list.");

        AppLog.Info(
            $"SetHouseCoordinator start → {zone.RoomName} @ {zone.IpAddress} uuid={zone.Uuid} " +
            $"(wasCoordinator={zone.IsCoordinator}, dailySubset={(allow is null ? "all" : string.Join("+", allow))})");

        // If Theater (etc.) is currently a slave of Office, it must become standalone first
        // or other rooms cannot join it as coordinator.
        await BecomeStandaloneCoordinatorAsync(zone, ct).ConfigureAwait(false);
        await Task.Delay(400, ct).ConfigureAwait(false);

        var join = await RegroupAllToUuidAsync(zone.Uuid, zone.RoomName, ct).ConfigureAwait(false);

        ActiveRoom = zone.RoomName;
        _controller = new SonosController(zone.IpAddress, zone.Uuid, _soap);
        SubscribeToActiveCoordinator();

        await Task.Delay(500, ct).ConfigureAwait(false);
        await RefreshAsync(zone.RoomName, ct).ConfigureAwait(false);

        var verify = VerifyHouseGrouping(zone.Uuid, zone.RoomName);
        LogGroupingVerification(zone.Uuid, zone.RoomName);

        var msg =
            $"Preferred coordinator → {zone.RoomName} ({zone.IpAddress}) · " +
            $"group={ActiveGroupLabel ?? zone.RoomName} · " +
            $"joined={join.Joined} fail={join.Failed.Count} · " +
            $"verify groups={verify.GroupCount} membersUnderCoord={verify.MembersUnderCoordinator} " +
            $"coordIsLeader={verify.CoordinatorIsLeader} ok={verify.Ok}";

        if (join.Failed.Count > 0)
            msg += " · joinFails=[" + string.Join("; ", join.Failed) + "]";

        if (!verify.Ok)
        {
            AppLog.Warn("SetHouseCoordinator verification FAILED: " + msg);
            throw new InvalidOperationException(
                "Regroup did not stick. " + msg +
                " Check Topology map — preferred room must show as COORDINATOR with others under it.");
        }

        AppLog.Info("SetHouseCoordinator OK: " + msg);
        return msg;
    }

    /// <summary>
    /// Live check: is <paramref name="coordinatorUuid"/> leading the intended group?
    /// Full-house: almost all visible rooms. Daily subset: all allow-listed rooms under the coordinator.
    /// </summary>
    public (bool Ok, int GroupCount, int MembersUnderCoordinator, bool CoordinatorIsLeader, string? LeaderRoom)
        VerifyHouseGrouping(string coordinatorUuid, string? expectedRoom = null)
    {
        var allow = _settings().EnsureShape().GetDailyGroupRoomAllowList();
        var visible = _zones.Where(z => !string.IsNullOrWhiteSpace(z.RoomName)).ToList();
        // Prefer topology snapshot when monitor has full members (incl bonded)
        var members = LastTopology?.Members
            .Where(m => !m.Invisible)
            .ToList();
        if (members is { Count: > 0 })
        {
            var scoped = allow is null
                ? members
                : members.Where(m => allow.Contains(m.RoomName)).ToList();
            var under = scoped.Count(m =>
                string.Equals(m.CoordinatorUuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase));
            var leader = members.FirstOrDefault(m =>
                string.Equals(m.Uuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase));
            var isLeader = leader is not null && leader.IsCoordinator;
            var groupCount = members.Select(m => m.GroupId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var scopedRooms = scoped.Select(m => m.RoomName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            bool ok;
            if (allow is not null)
            {
                // Subset: every intended room under this coordinator (allow 1 miss for flaky Wi-Fi).
                ok = isLeader && scopedRooms >= 1 && under >= Math.Max(1, scopedRooms - 1);
            }
            else
            {
                // Full house: one primary group with almost all visible rooms under this uuid
                var visibleRooms = members.Select(m => m.RoomName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                ok = isLeader && under >= Math.Max(2, visibleRooms - 1) && groupCount <= 2;
                if (!ok && isLeader && under >= visibleRooms - 2 && groupCount <= 3)
                    ok = under >= 3;
            }

            return (ok, groupCount, under, isLeader, leader?.RoomName);
        }

        var scopedZ = allow is null
            ? visible
            : visible.Where(z => allow.Contains(z.RoomName)).ToList();
        var underZ = scopedZ.Count(z =>
            string.Equals(z.CoordinatorUuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase));
        var leadZ = visible.FirstOrDefault(z =>
            string.Equals(z.Uuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase));
        var isLead = leadZ?.IsCoordinator == true;
        var gc = Groups.Count;
        var okZ = allow is null
            ? isLead && underZ >= Math.Max(2, visible.Count - 1)
            : isLead && underZ >= Math.Max(1, scopedZ.Count - 1);
        return (okZ, gc, underZ, isLead, leadZ?.RoomName);
    }

    private void LogGroupingVerification(string coordinatorUuid, string room)
    {
        var v = VerifyHouseGrouping(coordinatorUuid, room);
        var level = v.Ok ? "OK" : "FAIL";
        AppLog.Info(
            $"GroupVerify [{level}] preferred/target={room} uuid={coordinatorUuid} " +
            $"leader={v.LeaderRoom} isLeader={v.CoordinatorIsLeader} " +
            $"membersUnder={v.MembersUnderCoordinator} groups={v.GroupCount}");
    }

    private async Task BecomeStandaloneCoordinatorAsync(SonosZone zone, CancellationToken ct)
    {
        try
        {
            await _soap.InvokeAsync(
                zone.IpAddress, SonosService.AvTransport, "BecomeCoordinatorOfStandaloneGroup",
                [new("InstanceID", "0")], ct).ConfigureAwait(false);
            AppLog.Info($"BecomeCoordinatorOfStandaloneGroup OK: {zone.RoomName} @ {zone.IpAddress}");
        }
        catch (Exception ex)
        {
            // Already coordinator → often faults; not always fatal.
            AppLog.Warn(
                $"BecomeCoordinatorOfStandaloneGroup ({zone.RoomName}): {ex.Message}");
        }
    }

    private async Task<(int Joined, List<string> Failed)> RegroupAllToUuidAsync(
        string coordinatorUuid, string coordinatorRoom, CancellationToken ct)
    {
        var failed = new List<string>();
        var joined = 0;
        var allow = _settings().EnsureShape().GetDailyGroupRoomAllowList();

        // Distinct players by UUID (not room — stereo pairs).
        var allPlayers = _zones
            .Where(z => !string.IsNullOrWhiteSpace(z.Uuid) && !string.IsNullOrWhiteSpace(z.IpAddress))
            .GroupBy(z => z.Uuid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Join only Daily-allowlisted rooms (null allowlist = all). Always exclude the coordinator itself.
        var targets = allPlayers
            .Where(z => !string.Equals(z.Uuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase))
            .Where(z => allow is null || allow.Contains(z.RoomName))
            .ToList();

        // Leave rooms that must stay out of Daily (e.g. upstairs while daughter is visiting).
        var leave = allow is null
            ? []
            : allPlayers
                .Where(z => !string.Equals(z.Uuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase))
                .Where(z => !allow.Contains(z.RoomName))
                .Where(z => string.Equals(z.CoordinatorUuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(z.CoordinatorUuid, z.Uuid, StringComparison.OrdinalIgnoreCase) == false)
                .ToList();

        AppLog.Info(
            $"RegroupAllTo {coordinatorRoom} ({coordinatorUuid}): joining {targets.Count} player(s)" +
            (allow is null ? " (all speakers)" : $", leaving {leave.Count} out of Daily"));

        foreach (var zone in leave)
        {
            // Already alone — nothing to detach.
            if (string.Equals(zone.CoordinatorUuid, zone.Uuid, StringComparison.OrdinalIgnoreCase)
                && zone.IsCoordinator)
                continue;

            try
            {
                await BecomeStandaloneCoordinatorAsync(zone, ct).ConfigureAwait(false);
                AppLog.Info($"Daily leave: {zone.RoomName} detached from house group");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Daily leave FAILED {zone.RoomName}: {ex.Message}");
            }
        }

        foreach (var zone in targets)
        {
            var ok = false;
            Exception? last = null;
            for (var attempt = 1; attempt <= 3 && !ok; attempt++)
            {
                try
                {
                    await _soap.InvokeAsync(
                        zone.IpAddress, SonosService.AvTransport, "SetAVTransportURI",
                        [
                            new("InstanceID", "0"),
                            new("CurrentURI", $"x-rincon:{coordinatorUuid}"),
                            new("CurrentURIMetaData", ""),
                        ], ct).ConfigureAwait(false);
                    ok = true;
                    joined++;
                    if (attempt > 1)
                        AppLog.Info($"Join OK attempt {attempt}: {zone.RoomName} → {coordinatorRoom}");
                }
                catch (Exception ex)
                {
                    last = ex;
                    await Task.Delay(200 * attempt, ct).ConfigureAwait(false);
                }
            }

            if (!ok)
            {
                var err = $"{zone.RoomName}@{zone.IpAddress}: {last?.Message ?? "unknown"}";
                failed.Add(err);
                AppLog.Warn($"Join FAILED {coordinatorRoom}: {err}");
            }
        }

        return (joined, failed);
    }

    /// <summary>Joins every visible player to the given coordinator UUID (whole-house).</summary>
    public async Task GroupAllSpeakersToAsync(string coordinatorUuid, CancellationToken ct = default) =>
        await RegroupAllToUuidAsync(coordinatorUuid, coordinatorUuid, ct).ConfigureAwait(false);

    /// <summary>
    /// Renames a player room via DeviceProperties <c>SetZoneAttributes</c>
    /// (same API the Sonos app uses). Preserves icon / configuration when known.
    /// </summary>
    public async Task RenameZoneAsync(string ip, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip))
            throw new ArgumentException("IP is required.", nameof(ip));
        var name = (newName ?? "").Trim();
        if (name.Length == 0)
            throw new ArgumentException("New name is required.", nameof(newName));
        if (name.Length > 64)
            throw new ArgumentException("Name is too long (max 64).", nameof(newName));

        string icon = "";
        string config = "";
        try
        {
            var get = await _soap.InvokeAsync(
                ip, SonosService.DeviceProperties, "GetZoneAttributes",
                [], ct).ConfigureAwait(false);
            icon = SonosSoapClient.ReadValue(get, "CurrentIcon") ?? "";
            config = SonosSoapClient.ReadValue(get, "CurrentConfiguration") ?? "";
        }
        catch (Exception ex)
        {
            // Still try rename with empty icon/config — some firmware only needs the name.
            AppLog.Warn($"GetZoneAttributes @ {ip}: {ex.Message}");
        }

        await _soap.InvokeAsync(
            ip, SonosService.DeviceProperties, "SetZoneAttributes",
            [
                new("DesiredZoneName", name),
                new("DesiredIcon", icon),
                new("DesiredConfiguration", config),
            ],
            ct).ConfigureAwait(false);

        AppLog.Info($"RenameZone OK @ {ip} → “{name}”");
    }

    /// <summary>
    /// Requests a player reboot via the classic local endpoint
    /// <c>http://{ip}:1400/reboot</c>. Many firmwares accept a simple GET;
    /// connection drop / timeout after accept is treated as success (device is rebooting).
    /// Returns a short status string for the UI.
    /// </summary>
    public async Task<string> RebootPlayerAsync(string ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip))
            throw new ArgumentException("IP is required.", nameof(ip));

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        var url = $"http://{ip}:1400/reboot";
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            // 200/302/403-with-body sometimes still reboots on older FW; 200 is ideal.
            if (resp.IsSuccessStatusCode || code is 302 or 303)
            {
                AppLog.Info($"Reboot requested OK @ {ip} (HTTP {code})");
                return $"Reboot requested @ {ip} (HTTP {code}). Speaker will drop briefly.";
            }

            // Some modern FW want POST. Try once.
            using var post = await http.PostAsync(url, new System.Net.Http.StringContent(""), ct)
                .ConfigureAwait(false);
            var postCode = (int)post.StatusCode;
            if (post.IsSuccessStatusCode || postCode is 302 or 303)
            {
                AppLog.Info($"Reboot requested OK (POST) @ {ip} (HTTP {postCode})");
                return $"Reboot requested @ {ip} (POST HTTP {postCode}). Speaker will drop briefly.";
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var snippet = body.Length > 120 ? body[..120] + "…" : body;
            AppLog.Warn($"Reboot @ {ip} HTTP {code}/{postCode}: {snippet}");
            return $"Reboot may be blocked on this firmware (HTTP {code}). Try power-cycle if needed.";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            // Device often closes the TCP connection as it restarts — treat as accepted.
            AppLog.Info($"Reboot @ {ip}: connection dropped ({ex.Message}) — likely rebooting");
            return $"Reboot sent @ {ip} (connection dropped — speaker likely restarting).";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Info($"Reboot @ {ip}: timeout — likely rebooting");
            return $"Reboot sent @ {ip} (timeout — speaker likely restarting).";
        }
    }

    /// <summary>
    /// Nightly maintenance: re-discover, and if NOTHING is playing anywhere,
    /// silently regroup every speaker under one coordinator. With
    /// <paramref name="reshuffle"/>, also starts a fresh library shuffle
    /// afterward (this is the one case where the nightly reset starts
    /// playback — opt-in only). Returns a short status describing what happened.
    /// </summary>
    public async Task<string> NightlyResetAsync(bool reshuffle, CancellationToken ct = default)
    {
        await RefreshAsync(ActiveRoom, ct).ConfigureAwait(false);
        if (_controller is null)
            return "no speakers found";

        if (await IsAnythingPlayingAsync(ct).ConfigureAwait(false))
            return "skipped — music is playing";

        await GroupAllSpeakersAsync(ct).ConfigureAwait(false);

        if (!reshuffle)
            return "regrouped all speakers";

        var summary = await ShuffleWithHistoryAsync(ct).ConfigureAwait(false);
        return $"regrouped + reshuffled all speakers ({summary})";
    }

    /// <summary>
    /// Rebuild the queue from A:TRACKS using current shuffle settings
    /// (queue size, exclude played/skipped history, artist spread).
    /// </summary>
    public async Task<string> ShuffleWithHistoryAsync(CancellationToken ct = default)
    {
        if (_controller is null)
            throw new InvalidOperationException("No Sonos room is selected.");

        var s = _settings().EnsureShape();
        IReadOnlyCollection<string>? exclude = s.ShuffleExcludePlayed ? _playHistory.GetPlayedKeys() : null;
        var include = s.GetDailyShuffleIncludePrefixes();
        var result = await _controller.ShuffleMusicLibraryAsync(
            new ShuffleOptions
            {
                MaxQueueTracks = s.ShuffleQueueTracks,
                ExcludeUris = exclude,
                IncludePathPrefixes = include,
                AppendToQueue = false,
                ArtistSpread = s.ShuffleArtistSpread,
            },
            ct).ConfigureAwait(false);

        // New queue replaces the old one — restart session served set from this batch.
        ClearSessionServed();
        RememberServed(result.EnqueuedUris);
        _lastGenaNpSignature = null;
        _ignoreIndexResetUntilUtc = DateTime.UtcNow.AddSeconds(90);
        _prevQueueTrackIndex = 1;
        _prevQueueTrackTotal = result.Enqueued;
        _shuffleQueueState.RecordRebuild(result.Enqueued, "shuffle", result.EnqueuedUris);

        _playbackMode = "shuffle";
        _folderShufflePrefix = null;
        var dailyNote = include is null || include.Count == 0
            ? "scope=all"
            : $"scope={include.Count} folder(s)";
        var msg =
            $"browsed {result.Browsed}, scoped-out {result.ScopeFilteredCount}, queued {result.Enqueued} " +
            $"(candidates {result.CandidateCount}, excluded played {result.ExcludedCount}, " +
            $"{dailyNote}, history keys {_playHistory.PlayedDistinctCount}, history days {s.ShuffleHistoryDays})";
        AppLog.Info($"Shuffle rebuild: {msg}");
        return msg;
    }

    /// <summary>
    /// History-aware shuffle of one library folder (path prefix). Top-up stays in this folder
    /// until Daily shuffle / resume. Uses A:TRACKS + path filter (same engine as Daily).
    /// </summary>
    public async Task<string> PlayLibraryFolderAsync(
        LibraryService library,
        string folderPath,
        bool shuffle = true,
        CancellationToken ct = default)
    {
        if (_controller is null)
            throw new InvalidOperationException("No Sonos room is selected. Open HotSonos and pick a room.");
        if (library is null)
            throw new ArgumentNullException(nameof(library));

        folderPath = (folderPath ?? "").Trim().TrimEnd('\\', '/');
        if (folderPath.Length == 0)
            throw new ArgumentException("Folder path is required.", nameof(folderPath));

        var s = _settings().EnsureShape();
        var label = System.IO.Path.GetFileName(folderPath);
        if (string.IsNullOrWhiteSpace(label))
            label = folderPath;

        // Prefer cache for a quick empty check / toast counts; shuffle still uses Sonos browse + prefix.
        var cached = library.CountTracksUnderFolder(folderPath);

        IReadOnlyCollection<string>? exclude = s.ShuffleExcludePlayed ? _playHistory.GetPlayedKeys() : null;
        var result = await _controller.ShuffleMusicLibraryAsync(
            new ShuffleOptions
            {
                MaxQueueTracks = s.ShuffleQueueTracks,
                ExcludeUris = exclude,
                IncludePathPrefixes = [folderPath],
                AppendToQueue = false,
                ArtistSpread = s.ShuffleArtistSpread,
            },
            ct).ConfigureAwait(false);

        ClearSessionServed();
        RememberServed(result.EnqueuedUris);
        _ignoreIndexResetUntilUtc = DateTime.UtcNow.AddSeconds(90);
        _prevQueueTrackIndex = 1;
        _prevQueueTrackTotal = result.Enqueued;
        _shuffleQueueState.RecordRebuild(result.Enqueued, "folder", result.EnqueuedUris);
        _playbackMode = "folder";
        _folderShufflePrefix = folderPath;

        var msg =
            $"Folder · {label}: browsed {result.Browsed}, scoped-out {result.ScopeFilteredCount}, " +
            $"queued {result.Enqueued} (cache ~{cached}, excluded played {result.ExcludedCount})";
        AppLog.Info($"Play library folder: {msg}");
        return $"▶ {msg}";
    }

    /// <summary>
    /// Poll the coordinator for the current track and mark it played/skipped in history
    /// so the next rebuild/top-up will hard-exclude it.
    /// </summary>
    private async Task RecordCurrentTrackAsPlayedAsync(CancellationToken ct)
    {
        if (_controller is null) return;
        try
        {
            var uri = await _controller.GetCurrentTrackUriAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(uri))
            {
                // Fall back to last GENA snapshot if poll is empty mid-transition.
                uri = _lastEventUri;
            }

            if (string.IsNullOrWhiteSpace(uri)) return;
            _playHistory.RecordPlayed(uri);
            RememberServed([uri]);
            _lastEventUri = uri;
            _lastEventTrackKey = PlayHistoryStore.NormalizeKey(uri);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not record skipped track in play history", ex);
        }
    }

    private void ClearSessionServed()
    {
        lock (_servedGate)
            _sessionServedKeys.Clear();
    }

    private void RememberServed(IEnumerable<string> uris)
    {
        lock (_servedGate)
        {
            foreach (var u in uris)
            {
                var k = PlayHistoryStore.NormalizeKey(u);
                if (k.Length > 0)
                    _sessionServedKeys.Add(k);
            }
        }
    }

    private List<string> SnapshotSessionServed()
    {
        lock (_servedGate)
            return _sessionServedKeys.ToList();
    }

    /// <summary>Played history + tracks already enqueued this shuffle session.</summary>
    private IReadOnlyCollection<string>? BuildExcludeKeys(AppSettings s)
    {
        if (!s.ShuffleExcludePlayed)
            return null;

        var set = new HashSet<string>(_playHistory.GetPlayedKeys(), StringComparer.OrdinalIgnoreCase);
        foreach (var k in SnapshotSessionServed())
            set.Add(k);
        return set;
    }

    /// <summary>
    /// Play one library track (UNC or x-file-cifs). Replaces the queue.
    /// Does not wipe play history — resume_shuffle can start a fresh shuffle afterward.
    /// </summary>
    public async Task<string> PlayLibraryTrackAsync(
        string pathOrUri,
        string? title = null,
        string? artist = null,
        CancellationToken ct = default)
    {
        if (_controller is null)
            throw new InvalidOperationException("No Sonos room is selected. Open HotSonos and pick a room.");

        if (!SonosPath.TryToCifsUri(pathOrUri, out var cifs))
            throw new ArgumentException("Path must be a UNC path or x-file-cifs URI under the Sonos library.", nameof(pathOrUri));

        await _controller.PlayLibraryUriAsync(cifs, title, artist, ct).ConfigureAwait(false);
        _playbackMode = "special";
        RememberServed([cifs]);
        var label = string.IsNullOrWhiteSpace(title)
            ? System.IO.Path.GetFileName(pathOrUri)
            : (string.IsNullOrWhiteSpace(artist) ? title : $"{title} — {artist}");
        AppLog.Info($"Play library track: {cifs}");
        return $"▶ {label}";
    }

    /// <summary>
    /// Queue all library tracks with a catalog tag (label or key), optionally shuffled, and play.
    /// With <see cref="AppSettings.ContinueLibraryShuffleAfterSpecialPlay"/>, auto top-up
    /// continues into full-library shuffle when the tag queue runs low (same as one-shot).
    /// </summary>
    public async Task<string> PlayTaggedTracksAsync(
        LibraryService library,
        string tagToken,
        bool shuffle = true,
        CancellationToken ct = default)
    {
        if (_controller is null)
            throw new InvalidOperationException("No Sonos room is selected. Open HotSonos and pick a room.");
        if (library is null)
            throw new ArgumentNullException(nameof(library));

        var s = _settings().EnsureShape();
        var key = s.ResolveTagToken(tagToken);
        if (key is null)
            throw new InvalidOperationException($"Unknown tag “{tagToken}”. Use list_tags for labels.");

        var def = s.FindTag(key);
        var tagLabel = def?.Label ?? tagToken;
        var tracks = library.GetTracksWithTag(key);
        if (tracks.Count == 0)
            throw new InvalidOperationException($"No tracks in cache with tag “{tagLabel}”. Tag some music first.");

        var items = new List<(string CifsUri, string? Title, string? Artist)>(tracks.Count);
        foreach (var t in tracks)
        {
            if (!SonosPath.TryToCifsUri(t.Path, out var cifs))
                continue;
            items.Add((cifs, t.Title, t.Artist));
        }

        if (items.Count == 0)
            throw new InvalidOperationException($"Tag “{tagLabel}” matched tracks but none had a Sonos-playable path.");

        if (shuffle)
        {
            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        await _controller.PlayLibraryUrisAsync(items, ct).ConfigureAwait(false);
        _playbackMode = "special";
        RememberServed(items.Select(i => i.CifsUri));
        var continueHint = s.ContinueLibraryShuffleAfterSpecialPlay
            ? " · will top-up into library shuffle near end"
            : " · no library top-up after this queue";
        AppLog.Info($"Play tag “{tagLabel}”: queued {items.Count} (shuffle={shuffle})");
        return $"▶ {tagLabel}: {items.Count} track(s){(shuffle ? " shuffled" : "")}{continueHint}";
    }

    /// <summary>
    /// Queue all library tracks matching a standard Genre field label, optionally shuffled, and play.
    /// Same top-up-into-library-shuffle behavior as tag play when enabled in settings.
    /// </summary>
    public async Task<string> PlayGenreTracksAsync(
        LibraryService library,
        string genre,
        bool shuffle = true,
        CancellationToken ct = default)
    {
        if (_controller is null)
            throw new InvalidOperationException("No Sonos room is selected. Open HotSonos and pick a room.");
        if (library is null)
            throw new ArgumentNullException(nameof(library));

        genre = (genre ?? "").Trim();
        if (genre.Length == 0)
            throw new ArgumentException("Genre is required.", nameof(genre));

        var s = _settings().EnsureShape();
        var tracks = library.GetTracksWithGenre(genre);
        if (tracks.Count == 0)
            throw new InvalidOperationException(
                $"No tracks in cache with genre “{genre}”. Rescan the library if tags look wrong.");

        var items = new List<(string CifsUri, string? Title, string? Artist)>(tracks.Count);
        foreach (var t in tracks)
        {
            if (!SonosPath.TryToCifsUri(t.Path, out var cifs))
                continue;
            items.Add((cifs, t.Title, t.Artist));
        }

        if (items.Count == 0)
            throw new InvalidOperationException(
                $"Genre “{genre}” matched tracks but none had a Sonos-playable path.");

        if (shuffle)
        {
            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        await _controller.PlayLibraryUrisAsync(items, ct).ConfigureAwait(false);
        _playbackMode = "special";
        RememberServed(items.Select(i => i.CifsUri));
        var continueHint = s.ContinueLibraryShuffleAfterSpecialPlay
            ? " · will top-up into library shuffle near end"
            : " · no library top-up after this queue";
        AppLog.Info($"Play genre “{genre}”: queued {items.Count} (shuffle={shuffle})");
        return $"▶ Genre · {genre}: {items.Count} track(s){(shuffle ? " shuffled" : "")}{continueHint}";
    }

    /// <summary>Play a Sonos Favorite or saved Playlist by title (same catalog as Settings slots).</summary>
    public async Task<string> PlaySonosFavoriteByNameAsync(string title, CancellationToken ct = default)
    {
        if (_controller is null)
            throw new InvalidOperationException("No Sonos room is selected. Open HotSonos and pick a room.");
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required.", nameof(title));

        await _controller.PlayFavoriteByNameAsync(title.Trim(), ct).ConfigureAwait(false);
        // Don't auto top-up into library over a Sonos playlist/favorite session.
        _playbackMode = "sonos_fav";
        AppLog.Info($"Play Sonos favorite/playlist: {title}");
        return $"▶ {title.Trim()}";
    }

    /// <summary>
    /// Return to library shuffle after a one-shot (or anytime). Starts a <b>new</b>
    /// history-aware shuffle — not a restore of the previous queue order.
    /// </summary>
    public async Task<string> ResumeShuffleAsync(CancellationToken ct = default)
    {
        if (_controller is null)
            throw new InvalidOperationException("No Sonos room is selected. Open HotSonos and pick a room.");

        await GroupAllSpeakersAsync(ct).ConfigureAwait(false);
        var summary = await ShuffleWithHistoryAsync(ct).ConfigureAwait(false);
        return $"🔀 Resume shuffle → all speakers ({summary})";
    }

    /// <summary>
    /// Whether auto top-up may run for the current playback mode.
    /// Shuffle: always (if auto top-up on). Special (one track / tag queue): only when
    /// <see cref="AppSettings.ContinueLibraryShuffleAfterSpecialPlay"/> is true.
    /// </summary>
    private bool ShouldAutoTopUp(AppSettings s)
    {
        if (string.Equals(_playbackMode, "shuffle", StringComparison.OrdinalIgnoreCase))
            return true;
        // Folder mode always tops up inside the same folder (mood stays mood).
        if (string.Equals(_playbackMode, "folder", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(_playbackMode, "special", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_playbackMode, "one_shot", StringComparison.OrdinalIgnoreCase))
            return s.ContinueLibraryShuffleAfterSpecialPlay;
        return false;
    }

    /// <summary>
    /// Append another random batch excluding play/skip history and tracks already
    /// enqueued this session — when the queue is nearly empty.
    /// </summary>
    public async Task TryTopUpQueueAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _topUpInFlight, 1, 0) != 0)
            return;

        try
        {
            if (_controller is null)
                return;

            var s = _settings().EnsureShape();
            if (!s.ShuffleAutoTopUp || !ShouldAutoTopUp(s))
                return;

            var exclude = BuildExcludeKeys(s);
            IReadOnlyCollection<string>? include;
            if (string.Equals(_playbackMode, "folder", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_folderShufflePrefix))
            {
                include = [_folderShufflePrefix];
            }
            else
            {
                include = s.GetDailyShuffleIncludePrefixes();
            }

            var result = await _controller.ShuffleMusicLibraryAsync(
                new ShuffleOptions
                {
                    MaxQueueTracks = s.ShuffleTopUpTracks,
                    ExcludeUris = exclude,
                    IncludePathPrefixes = include,
                    AppendToQueue = true,
                    ArtistSpread = s.ShuffleArtistSpread,
                },
                ct).ConfigureAwait(false);

            RememberServed(result.EnqueuedUris);
            _shuffleQueueState.RecordTopUp(result.Enqueued);

            // Special → daily after first library top-up. Folder mode stays folder.
            if (string.Equals(_playbackMode, "special", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_playbackMode, "one_shot", StringComparison.OrdinalIgnoreCase))
            {
                _playbackMode = "shuffle";
                _folderShufflePrefix = null;
            }

            var session = SnapshotSessionServed().Count;
            AppLog.Info(
                $"Shuffle top-up: appended {result.Enqueued} " +
                $"(excluded {result.ExcludedCount}, scoped-out {result.ScopeFilteredCount}, " +
                $"mode={_playbackMode}, history {_playHistory.PlayedDistinctCount}, session served {session})");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Shuffle top-up failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _topUpInFlight, 0);
        }
    }

    /// <summary>True if any group coordinator is currently playing or mid-transition.</summary>
    public async Task<bool> IsAnythingPlayingAsync(CancellationToken ct = default)
    {
        foreach (var group in Groups)
        {
            try
            {
                var controller = new SonosController(group.CoordinatorIp, group.CoordinatorUuid, _soap);
                var state = await controller.GetTransportStateAsync(ct).ConfigureAwait(false);
                if (state is SonosTransportState.Playing or SonosTransportState.Transitioning)
                    return true;
            }
            catch
            {
                // If we can't read a coordinator, err on the safe side and treat as "not playing"
                // only for that one; a real playing group would normally answer.
            }
        }
        return false;
    }

    private void RebuildGroups()
    {
        var totalVisible = _zones.Count;

        Groups = _zones
            .GroupBy(z => z.CoordinatorUuid, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var coord = g.FirstOrDefault(z => z.IsCoordinator) ?? g.First();
                var count = g.Count();
                var name = count == totalVisible && totalVisible > 1
                    ? "All Speakers"
                    : count > 1
                        ? $"{coord.RoomName} + {count - 1}"
                        : coord.RoomName;
                return new SonosGroup(name, coord.RoomName, coord.CoordinatorUuid, coord.CoordinatorIpAddress, count);
            })
            .OrderByDescending(g => g.MemberCount)
            .ThenBy(g => g.CoordinatorRoom, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RebuildController(bool onlyIfCoordinatorChanged = false)
    {
        var group =
            Groups.FirstOrDefault(g => string.Equals(g.CoordinatorRoom, ActiveRoom, StringComparison.OrdinalIgnoreCase))
            ?? Groups.FirstOrDefault(g => ContainsRoom(g, ActiveRoom));

        string? nextIp = null;
        string? nextUuid = null;
        if (group is not null)
        {
            nextIp = group.CoordinatorIp;
            nextUuid = group.CoordinatorUuid;
        }
        else
        {
            var zone = _zones.FirstOrDefault(z => string.Equals(z.RoomName, ActiveRoom, StringComparison.OrdinalIgnoreCase));
            if (zone is not null)
            {
                nextIp = zone.CoordinatorIpAddress;
                nextUuid = zone.CoordinatorUuid;
            }
        }

        if (onlyIfCoordinatorChanged
            && _controller is not null
            && string.Equals(_controller.CoordinatorIp, nextIp, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_controller.CoordinatorUuid, nextUuid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (nextIp is null || nextUuid is null)
        {
            _controller = null;
            return;
        }

        _controller = new SonosController(nextIp, nextUuid, _soap);
        SubscribeToActiveCoordinator();
    }

    private void SubscribeToActiveCoordinator()
    {
        if (_controller is null)
            return;

        if (UseNowPlayingPoll)
            EnsureNowPlayingPoller();

        if (!UseGenaSubscriptions)
            return;

        _ = _events.SubscribeAsync(_controller.CoordinatorIp);
    }

    private bool ContainsRoom(SonosGroup group, string? room) =>
        room is not null && _zones.Any(z =>
            string.Equals(z.CoordinatorUuid, group.CoordinatorUuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(z.RoomName, room, StringComparison.OrdinalIgnoreCase));
}
