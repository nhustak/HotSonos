using System.IO;
using HotSonos.App.Infrastructure;
using HotSonos.App.Models;

namespace HotSonos.App.Library;

/// <summary>
/// Orchestrates filesystem scan → SQLite cache for Sonos library roots
/// (discovered from speakers and/or saved in settings).
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly LibraryDb _db;
    private readonly Func<AppSettings> _settings;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>>? _discoverRootsFromSonos;
    private readonly Action? _persistSettings;
    private readonly object _scanGate = new();
    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;

    private bool _isScanning;
    private string? _phase;
    private DateTime? _lastStarted;
    private DateTime? _lastFinished;
    private string? _lastError;
    private int _lastSeen;
    private int _lastUpdated;
    private int _lastSkipped;
    private int _lastRemoved;
    private int _lastErrors;

    public LibraryService(
        Func<AppSettings> settings,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? discoverRootsFromSonos = null,
        Action? persistSettings = null,
        string? databasePath = null)
    {
        _settings = settings;
        _discoverRootsFromSonos = discoverRootsFromSonos;
        _persistSettings = persistSettings;
        _db = new LibraryDb(databasePath);
        _db.Open();
        LoadLastScanMeta();
    }

    public string DatabasePath => _db.DatabasePath;

    public bool IsScanning
    {
        get { lock (_scanGate) return _isScanning; }
    }

    public LibraryStatus GetStatus()
    {
        var s = _settings().EnsureShape();
        lock (_scanGate)
        {
            return new LibraryStatus
            {
                IsScanning = _isScanning,
                TrackCount = _db.CountTracks(),
                SonosUnplayableCount = _db.CountSonosUnplayable(),
                RootsConfigured = s.SonosLibraryRoots.Count,
                Roots = s.SonosLibraryRoots.ToList(),
                MasterRoot = s.MasterLibraryRoot,
                DatabasePath = _db.DatabasePath,
                LastScanStartedUtc = _lastStarted,
                LastScanFinishedUtc = _lastFinished,
                LastScanError = _lastError,
                LastScanFilesSeen = _lastSeen,
                LastScanFilesUpdated = _lastUpdated,
                LastScanFilesSkippedUnchanged = _lastSkipped,
                LastScanFilesRemoved = _lastRemoved,
                LastScanErrors = _lastErrors,
                Phase = _phase,
            };
        }
    }

    /// <summary>
    /// Starts a background full rescan. If no roots are saved, discovers them from
    /// Sonos <c>A:TRACKS</c> first (when a discover callback is wired).
    /// </summary>
    public (bool started, string message) RequestRescan(bool forceAll = false, bool rediscoverRoots = false)
    {
        lock (_scanGate)
        {
            if (_isScanning)
                return (false, "Scan already in progress.");

            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;
            _isScanning = true;
            _phase = "starting";
            _lastError = null;
            _scanTask = Task.Run(() => RunScanPipelineAsync(forceAll, rediscoverRoots, ct), ct);
            return (true, rediscoverRoots || _settings().EnsureShape().SonosLibraryRoots.Count == 0
                ? "Discovering library roots from Sonos, then scanning…"
                : forceAll
                    ? "Full rescan started (force re-read all tags)."
                    : "Rescan started (skip unchanged files).");
        }
    }

    /// <summary>Discover Music Library UNC roots from Sonos and save them into settings.</summary>
    public async Task<(bool ok, string message, IReadOnlyList<string> roots)> DiscoverRootsFromSonosAsync(
        CancellationToken ct = default)
    {
        if (_discoverRootsFromSonos is null)
            return (false, "Sonos discovery is not wired.", []);

        try
        {
            SetPhase("discovering-roots");
            var roots = await _discoverRootsFromSonos(ct).ConfigureAwait(false);
            if (roots.Count == 0)
                return (false, "Sonos returned no x-file-cifs library tracks — is a Music Library share indexed?", []);

            var s = _settings().EnsureShape();
            s.SonosLibraryRoots = roots.ToList();
            try { _persistSettings?.Invoke(); }
            catch (Exception ex) { AppLog.Warn("Persist after root discovery failed", ex); }

            AppLog.Info($"Discovered library roots from Sonos: {string.Join(" | ", roots)}");
            return (true, $"Discovered {roots.Count} root(s) from Sonos.", roots);
        }
        catch (Exception ex)
        {
            AppLog.Error("Discover library roots from Sonos failed", ex);
            return (false, ex.Message, []);
        }
        finally
        {
            lock (_scanGate)
            {
                if (!_isScanning)
                    _phase = null;
            }
        }
    }

    private async Task RunScanPipelineAsync(bool forceAll, bool rediscoverRoots, CancellationToken ct)
    {
        try
        {
            var s = _settings().EnsureShape();
            if (rediscoverRoots || s.SonosLibraryRoots.Count == 0)
            {
                var (ok, message, roots) = await DiscoverRootsFromSonosAsync(ct).ConfigureAwait(false);
                if (!ok || roots.Count == 0)
                {
                    lock (_scanGate)
                    {
                        _isScanning = false;
                        _phase = null;
                        _lastError = message;
                        _lastFinished = DateTime.UtcNow;
                    }
                    try { _db.SetMeta("last_scan_error", message); } catch { /* ignore */ }
                    AppLog.Warn($"Library scan aborted: {message}");
                    return;
                }
                s = _settings().EnsureShape();
            }

            RunScan(s.SonosLibraryRoots.ToList(), forceAll, ct);
        }
        catch (Exception ex)
        {
            AppLog.Error("Library scan pipeline failed", ex);
            lock (_scanGate)
            {
                _isScanning = false;
                _phase = null;
                _lastError = ex.Message;
                _lastFinished = DateTime.UtcNow;
            }
        }
    }

    public IReadOnlyList<LibraryTrack> Search(string? query, int limit = 25, int offset = 0, bool sonosUnplayableOnly = false) =>
        _db.Search(query, limit, offset, sonosUnplayableOnly);

    public LibraryTrack? GetTrack(string path) => _db.GetByPath(path);

    public LibraryTrack? FindBySonosUri(string? uri) => _db.FindBySonosUriOrUnc(uri);

    public bool NeedsAudioPropsRescan() =>
        _db.CountTracks() > 0 && _db.HasTracksMissingAudioProps();

    /// <summary>
    /// Write tags into the file on the Sonos library share, then refresh the SQLite row.
    /// Path must be under a configured Sonos library root (or resolvable from cache).
    /// Master dual-write is step 4 — not done here.
    /// </summary>
    public TagWriteResult SetTags(string path, TrackTagUpdate update, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new TagWriteResult { Ok = false, Path = path ?? "", Error = "path is required", Message = "path is required" };

        path = path.Trim();
        var s = _settings().EnsureShape();
        var roots = s.SonosLibraryRoots;
        if (roots.Count == 0)
            return new TagWriteResult
            {
                Ok = false,
                Path = path,
                Error = "No Sonos library roots configured. Discover from Sonos first.",
                Message = "No Sonos library roots configured.",
            };

        // Prefer full path from cache if the caller passed a relative or partial path.
        var cached = _db.GetByPath(path) ?? _db.FindBySonosUriOrUnc(path);
        var fullPath = cached?.Path ?? path;
        if (!Path.IsPathRooted(fullPath) && !fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return new TagWriteResult { Ok = false, Path = fullPath, Error = "Path must be absolute/UNC.", Message = "Path must be absolute/UNC." };

        try { fullPath = Path.GetFullPath(fullPath); }
        catch { /* keep as-is for UNC edge cases */ }

        if (!IsUnderAnyRoot(fullPath, roots))
        {
            return new TagWriteResult
            {
                Ok = false,
                Path = fullPath,
                Error = "Path is not under a configured Sonos library root.",
                Message = "Path is not under a configured Sonos library root.",
            };
        }

        var root = roots.First(r => IsUnderRoot(fullPath, r));
        var result = LibraryTagWriter.Write(fullPath, update, dryRun, root);

        if (result.Ok && !result.DryRun && result.TrackAfter is not null)
        {
            try { _db.UpsertTracks([result.TrackAfter]); }
            catch (Exception ex)
            {
                AppLog.Warn("Cache refresh after tag write failed", ex);
            }
        }
        else if (result.Ok && !result.DryRun)
        {
            // Re-read even if writer didn't return a track
            var again = LibraryTagReader.TryRead(fullPath, root, DateTime.UtcNow);
            if (again is not null)
            {
                try { _db.UpsertTracks([again]); }
                catch (Exception ex) { AppLog.Warn("Cache refresh after tag write failed", ex); }
                return new TagWriteResult
                {
                    Ok = result.Ok,
                    Path = result.Path,
                    DryRun = result.DryRun,
                    Message = result.Message,
                    Error = result.Error,
                    Changes = result.Changes,
                    TrackAfter = again,
                };
            }
        }

        return result;
    }

    private static bool IsUnderAnyRoot(string fullPath, IReadOnlyList<string> roots) =>
        roots.Any(r => IsUnderRoot(fullPath, r));

    private static bool IsUnderRoot(string fullPath, string root)
    {
        try
        {
            var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = fullPath;
            // UNC-safe ordinal ignore case
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(
                       full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void RunScan(List<string> roots, bool forceAll, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var seen = 0;
        var updated = 0;
        var skipped = 0;
        var removed = 0;
        var errors = 0;
        string? error = null;

        try
        {
            SetPhase("enumerating");
            lock (_scanGate) _lastStarted = started;

            var existing = forceAll
                ? new Dictionary<string, (long Size, DateTime MtimeUtc)>(StringComparer.OrdinalIgnoreCase)
                : _db.LoadFingerprints(roots);

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var batch = new List<LibraryTrack>(64);
            const int BatchSize = 50;

            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root))
                {
                    // Sonos can index a share this PC cannot open (credentials / hostsallow).
                    var hint =
                        $"Library root unreachable from this PC: {root}. " +
                        "Sonos discovered it from A:TRACKS, but Windows cannot open the UNC path " +
                        "(map the share or store SMB credentials for this user).";
                    AppLog.Warn(hint);
                    error ??= hint;
                    errors++;
                    continue;
                }

                SetPhase($"scanning:{root}");
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                        .Where(f => LibraryTagReader.AudioExtensions.Contains(Path.GetExtension(f)));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Library enumerate failed: {root}", ex);
                    errors++;
                    continue;
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    seen++;

                    try
                    {
                        var fi = new FileInfo(file);
                        var fullPath = fi.FullName;
                        keep.Add(fullPath);

                        if (!forceAll
                            && existing.TryGetValue(fullPath, out var fp)
                            && fp.Size == fi.Length
                            && AlmostSameMtime(fp.MtimeUtc, fi.LastWriteTimeUtc))
                        {
                            skipped++;
                            continue;
                        }

                        var track = LibraryTagReader.TryRead(fullPath, root, DateTime.UtcNow);
                        if (track is null)
                        {
                            errors++;
                            continue;
                        }

                        batch.Add(track);
                        updated++;
                        if (batch.Count >= BatchSize)
                        {
                            _db.UpsertTracks(batch);
                            batch.Clear();
                            SetPhase($"scanning:{root} seen={seen} updated={updated}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        AppLog.Warn($"Library file failed: {file}", ex);
                    }
                }
            }

            if (batch.Count > 0)
                _db.UpsertTracks(batch);

            SetPhase("pruning");
            removed = _db.DeleteMissing(roots, keep);

            _db.SetMeta("last_scan_started_utc", started.ToString("o"));
            _db.SetMeta("last_scan_finished_utc", DateTime.UtcNow.ToString("o"));
            _db.SetMeta("last_scan_error", null);
            _db.SetMeta("last_scan_files_seen", seen.ToString());
            _db.SetMeta("last_scan_files_updated", updated.ToString());
            _db.SetMeta("last_scan_files_skipped", skipped.ToString());
            _db.SetMeta("last_scan_files_removed", removed.ToString());
            _db.SetMeta("last_scan_errors", errors.ToString());

            AppLog.Info(
                $"Library scan done: seen={seen} updated={updated} skipped={skipped} removed={removed} errors={errors}");
        }
        catch (OperationCanceledException)
        {
            error = "Scan cancelled.";
            AppLog.Info("Library scan cancelled");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error("Library scan failed", ex);
            try { _db.SetMeta("last_scan_error", error); } catch { /* ignore */ }
        }
        finally
        {
            lock (_scanGate)
            {
                _isScanning = false;
                _phase = null;
                _lastFinished = DateTime.UtcNow;
                _lastError = error;
                _lastSeen = seen;
                _lastUpdated = updated;
                _lastSkipped = skipped;
                _lastRemoved = removed;
                _lastErrors = errors;
            }
        }
    }

    private static bool AlmostSameMtime(DateTime a, DateTime b) =>
        Math.Abs((a.ToUniversalTime() - b.ToUniversalTime()).TotalSeconds) < 2;

    private void SetPhase(string phase)
    {
        lock (_scanGate) _phase = phase;
    }

    private void LoadLastScanMeta()
    {
        try
        {
            if (DateTime.TryParse(_db.GetMeta("last_scan_started_utc"), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var started))
                _lastStarted = started;
            if (DateTime.TryParse(_db.GetMeta("last_scan_finished_utc"), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var finished))
                _lastFinished = finished;
            _lastError = _db.GetMeta("last_scan_error");
            if (int.TryParse(_db.GetMeta("last_scan_files_seen"), out var seen)) _lastSeen = seen;
            if (int.TryParse(_db.GetMeta("last_scan_files_updated"), out var u)) _lastUpdated = u;
            if (int.TryParse(_db.GetMeta("last_scan_files_skipped"), out var sk)) _lastSkipped = sk;
            if (int.TryParse(_db.GetMeta("last_scan_files_removed"), out var r)) _lastRemoved = r;
            if (int.TryParse(_db.GetMeta("last_scan_errors"), out var e)) _lastErrors = e;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Library meta load failed", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            _scanCts?.Cancel();
            _scanTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* exit */ }

        _scanCts?.Dispose();
        _db.Dispose();
    }
}
