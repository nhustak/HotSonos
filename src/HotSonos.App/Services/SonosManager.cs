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
/// App-facing wrapper over the Core UPnP client. Holds the discovered topology
/// as groups and turns <see cref="HotsonosAction"/> into Sonos commands. Cheap
/// to call repeatedly; discovery is cached until refreshed.
/// </summary>
public sealed class SonosManager
{
    private readonly SonosSoapClient _soap = new();
    private readonly SonosDiscovery _discovery;
    private readonly SonosEventSubscriber _events = new();
    private readonly PlayHistoryStore _playHistory;
    private readonly PlayEventLog _playEvents;
    private readonly TopologyEventLog _topologyEvents;
    private readonly Func<AppSettings> _settings;

    private IReadOnlyList<SonosZone> _zones = [];
    private SonosController? _controller;
    private SonosTopologySnapshot? _lastTopology;

    private IReadOnlyList<string> _offline = [];
    private bool _topologySeen;
    private int _topUpInFlight; // 0/1
    private int _recoverInFlight; // 0/1
    private int _nowPlayingPollInFlight; // 0/1
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private System.Threading.Timer? _nowPlayingPollTimer;
    private DateTime _lastRecoverUtc = DateTime.MinValue;

    /// <summary>
    /// When false (default), do not use Sonos GENA TCP callbacks — they hard-killed the process
    /// around subscription renew (~2.5–5 min). Poll GetPositionInfo instead until GENA is rewritten.
    /// </summary>
    public static bool UseGenaSubscriptions { get; set; }

    /// <summary>
    /// When false, do not poll now-playing either (hotkeys/SOAP control only). Used to isolate
    /// whether transport polling is involved in the ~5 min hard death.
    /// </summary>
    public static bool UseNowPlayingPoll { get; set; } = true; // set false only for ultra-minimal isolation
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

    /// <summary>True after a library shuffle until a special play replaces the queue.</summary>
    public bool ShuffleSessionActive =>
        string.Equals(_playbackMode, "shuffle", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when shuffle or special play is active (resume_shuffle is meaningful).</summary>
    public bool CanResumeShuffle =>
        string.Equals(_playbackMode, "special", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_playbackMode, "one_shot", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_playbackMode, "shuffle", StringComparison.OrdinalIgnoreCase);

    public object GetPlaybackSessionSnapshot() => new
    {
        mode = _playbackMode,
        shuffleSessionActive = ShuffleSessionActive,
        canResumeShuffle = CanResumeShuffle,
        continueLibraryShuffleAfterSpecialPlay = _settings().EnsureShape().ContinueLibraryShuffleAfterSpecialPlay,
        folderPrefix = _folderShufflePrefix,
        note = "shuffle = Daily mix; folder = one library path (top-up stays there); special = tag/genre/one-shot (top-up may enter Daily).",
    };

    /// <summary>Raised when the active coordinator pushes a now-playing change.</summary>
    public event Action<NowPlaying>? NowPlayingChanged;

    /// <summary>Raised when the speaker topology changes (regroup / drop / return).</summary>
    public event Action? TopologyChanged;

    /// <summary>Raised when a speaker drops off (false) or comes back (true): (roomName, isOnline).</summary>
    public event Action<string, bool>? SpeakerAvailabilityChanged;

    /// <summary>Rooms currently reported as vanished/offline by Sonos.</summary>
    public IReadOnlyList<string> OfflineSpeakers => _offline;

    /// <summary>Number of visible zones in the last topology snapshot.</summary>
    public int GetZoneCount() => _zones.Count;

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
        TopologyEventLog? topologyEvents = null)
    {
        _settings = settings ?? AppSettings.CreateDefault;
        _playHistory = playHistory ?? new PlayHistoryStore(() => _settings().EnsureShape().ShuffleHistoryDays);
        _playEvents = playEvents ?? new PlayEventLog();
        _topologyEvents = topologyEvents ?? new TopologyEventLog();
        _discovery = new SonosDiscovery(_soap);
        _events.NowPlayingChanged += HandleNowPlayingSnapshot;
        _events.TopologyChanged += OnTopologyEvent;

        if (!UseGenaSubscriptions && UseNowPlayingPoll)
            EnsureNowPlayingPoller();
        else if (!UseGenaSubscriptions)
            AppLog.Info("GENA OFF and now-playing poll OFF — control-only stability mode");
    }

    /// <summary>Shared path for GENA or poll-based now-playing.</summary>
    private void HandleNowPlayingSnapshot(NowPlaying np)
    {
        try
        {
            var sig = GenaNowPlayingSignature(np);
            if (string.Equals(sig, _lastGenaNpSignature, StringComparison.Ordinal))
                return;
            _lastGenaNpSignature = sig;

            var prevState = _lastEventState;
            ObservePlayLifecycle(np);

            MaybeRearmShuffleFromLibraryQueue(np);

            if (!string.IsNullOrWhiteSpace(np.TrackUri)
                && np.State is SonosTransportState.Playing or SonosTransportState.Transitioning)
            {
                _playHistory.RecordPlayed(np.TrackUri);
            }

            var s = _settings().EnsureShape();
            if (s.ShuffleAutoTopUp
                && ShouldAutoTopUp(s)
                && np.State is SonosTransportState.Playing or SonosTransportState.Transitioning
                && np.IsNearQueueEnd(s.ShuffleTopUpWhenRemaining))
            {
                _ = TryTopUpQueueAsync();
            }

            _ = MaybeRecoverPlaybackAsync(np, prevState);

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

        AppLog.Info("GENA subscriptions OFF — polling now-playing every 5s (crash isolation)");
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
    /// Pulls full topology (incl. Sub / bonded) and logs a diff — only when monitor is enabled.
    /// </summary>
    public async Task ObserveTopologyAsync(string source = "refresh", CancellationToken ct = default)
    {
        if (!_settings().EnsureShape().TopologyMonitorEnabled)
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
            _lastTopology = snap;
            _topologyEvents.Observe(snap, source);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Topology observe failed ({source})", ex);
        }
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

    // ---- Volume (group-wide) ----------------------------------------------
    // Group-volume WRITE actions (SetGroupVolume/SetGroupMute) return 803 on
    // systems with a fixed-volume member, so we nudge each player via
    // per-player RenderingControl.
    //
    // Snappy hotkeys: await ONLY the coordinator (what you hear at Office). Fan
    // out to other rooms in the background with a short timeout. Waiting on all
    // members made volume feel multi-second when Kitchen/etc. were flaky (TCP
    // hang ignores CancelAfter until connect fails ~2–3s each batch).

    /// <summary>
    /// Adjusts volume by <paramref name="delta"/>. Returns coordinator Master % for toast.
    /// Other group members are updated in the background (best-effort).
    /// </summary>
    private async Task<int> ChangeVolumeAsync(int delta, CancellationToken ct)
    {
        var members = ActiveGroupMemberIps();
        var coordIp = _controller?.CoordinatorIp;

        // Background: everyone except coordinator — do not await.
        foreach (var ip in members)
        {
            if (coordIp is not null
                && string.Equals(ip, coordIp, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = ip;
            _ = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await AdjustMemberVolumeAsync(target, delta, cts.Token).ConfigureAwait(false);
            });
        }

        // Foreground: coordinator only (fast path).
        if (coordIp is not null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            await AdjustMemberVolumeAsync(coordIp, delta, cts.Token).ConfigureAwait(false);
        }

        return await GetCoordinatorMasterVolumeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Public volume step for coalesced hotkeys. Prefer one call with a combined delta
    /// over many stacked VolumeUp actions (which walked volume to 100% during lag).
    /// </summary>
    public Task<int> AdjustVolumeByAsync(int delta, CancellationToken ct = default) =>
        ChangeVolumeAsync(delta, ct);

    private async Task AdjustMemberVolumeAsync(string ip, int delta, CancellationToken ct)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetRelativeVolume",
                [
                    new("InstanceID", "0"),
                    new("Channel", "Master"),
                    new("Adjustment", delta.ToString()),
                ], ct).ConfigureAwait(false);
        }
        catch
        {
            // Fixed-volume members, offline, or timed out — ignore.
        }
    }

    /// <summary>Coordinator Master volume for toast (short timeout).</summary>
    private async Task<int> GetCoordinatorMasterVolumeAsync(CancellationToken ct)
    {
        if (_controller is null)
            return 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        try
        {
            var r = await _soap.InvokeAsync(
                _controller.CoordinatorIp, SonosService.RenderingControl, "GetVolume",
                [new("InstanceID", "0"), new("Channel", "Master")], cts.Token).ConfigureAwait(false);
            return int.TryParse(SonosSoapClient.ReadValue(r, "CurrentVolume"), out var v) ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Sets EVERY visible speaker (across all groups) to the absolute volume
    /// (plus any per-room offset) and unmutes them. Returns the count of speakers
    /// that accepted the change (fixed-volume members are not counted).
    /// </summary>
    public async Task<int> LevelAllVolumesAsync(int percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
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
        return results.Count(ok => ok);
    }

    /// <returns>True when the speaker accepted the volume (and unmute) change.</returns>
    private async Task<bool> SetMemberVolumeAsync(string ip, int percent, CancellationToken ct)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetVolume",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredVolume", percent.ToString())], ct).ConfigureAwait(false);
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
        }
        catch
        {
            // Fixed-volume members (Sub/Port/Amp line-out) reject volume changes; ignore them.
        }
    }

    /// <summary>Mutes/unmutes one speaker.</summary>
    public async Task SetSpeakerMuteAsync(string ip, bool mute, CancellationToken ct = default)
    {
        try
        {
            await _soap.InvokeAsync(ip, SonosService.RenderingControl, "SetMute",
                [new("InstanceID", "0"), new("Channel", "Master"), new("DesiredMute", mute ? "1" : "0")], ct).ConfigureAwait(false);
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
        var s = _settings().EnsureShape();
        await Task.WhenAll(ips.Select(ip =>
        {
            var room = _zones.FirstOrDefault(z =>
                string.Equals(z.IpAddress, ip, StringComparison.OrdinalIgnoreCase))?.RoomName;
            var actual = s.ApplyVolumeOffset(room, percent);
            return SetMemberVolumeAsync(ip, actual, ct);
        })).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls every visible player into the active group's coordinator so that
    /// subsequent playback covers all speakers. Idempotent; tolerates individual
    /// join failures. The coordinator's IP/UUID are unchanged, so the cached
    /// controller stays valid.
    /// </summary>
    public Task GroupAllSpeakersAsync(CancellationToken ct = default) =>
        _controller is null
            ? Task.CompletedTask
            : GroupAllSpeakersToAsync(_controller.CoordinatorUuid, ct);

    /// <summary>Joins every visible player to the given coordinator UUID (whole-house).</summary>
    public async Task GroupAllSpeakersToAsync(string coordinatorUuid, CancellationToken ct = default)
    {
        if (_zones.Count == 0 || string.IsNullOrWhiteSpace(coordinatorUuid))
            return;

        foreach (var zone in _zones)
        {
            if (string.Equals(zone.Uuid, coordinatorUuid, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                await _soap.InvokeAsync(
                    zone.IpAddress, SonosService.AvTransport, "SetAVTransportURI",
                    [
                        new("InstanceID", "0"),
                        new("CurrentURI", $"x-rincon:{coordinatorUuid}"),
                        new("CurrentURIMetaData", ""),
                    ], ct).ConfigureAwait(false);
            }
            catch
            {
                // One speaker failing to join shouldn't abort the whole-house grouping.
            }
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

        if (!UseGenaSubscriptions)
        {
            if (UseNowPlayingPoll)
                EnsureNowPlayingPoller();
            return;
        }

        _ = _events.SubscribeAsync(_controller.CoordinatorIp);
    }

    private bool ContainsRoom(SonosGroup group, string? room) =>
        room is not null && _zones.Any(z =>
            string.Equals(z.CoordinatorUuid, group.CoordinatorUuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(z.RoomName, room, StringComparison.OrdinalIgnoreCase));
}
