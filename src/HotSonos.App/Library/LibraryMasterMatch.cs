using System.Diagnostics;
using System.IO;
using HotSonos.App.Infrastructure;

namespace HotSonos.App.Library;

/// <summary>How a Sonos-library track was matched to a master-tree twin.</summary>
public enum MasterMatchKind
{
    /// <summary>No twin (or matching not attempted).</summary>
    None,
    /// <summary>Stored <see cref="LibraryTrack.MasterPath"/> link.</summary>
    Linked,
    /// <summary>Same relative path under master root.</summary>
    RelativePath,
    /// <summary>Same relative path with alternate extension (.flac ↔ .mp3).</summary>
    RelativePathAltExt,
    /// <summary>Trailing path segments under master (folder structure differs at the top).</summary>
    PathSuffix,
    /// <summary>Unique filename under master.</summary>
    FilenameUnique,
    /// <summary>Scored among filename candidates via tags/path/duration.</summary>
    Metadata,
    /// <summary>Multiple candidates; no safe auto-choice.</summary>
    Ambiguous,
    /// <summary>Master root configured but not reachable from this PC.</summary>
    Offline,
}

/// <summary>Result of locating a master twin for dual-write / linking.</summary>
public sealed class MasterMatchResult
{
    public MasterMatchKind Kind { get; init; } = MasterMatchKind.None;
    public string? Path { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> Candidates { get; init; } = [];

    public bool Found =>
        Path is not null
        && Kind is not MasterMatchKind.None
        and not MasterMatchKind.Ambiguous
        and not MasterMatchKind.Offline;
}

/// <summary>
/// Finds a twin under <c>MasterLibraryRoot</c> for dual-write of tags.
/// Strategy (spec §7.4): stored link → relative path → path suffix →
/// scoped artist/album name search → bounded filename walk + metadata score.
/// Full-tree walks are time-budgeted so a huge/slow master share cannot freeze the app.
/// Content hash is skipped: master is often a different encode (hi-res) of the same work.
/// </summary>
public static class LibraryMasterMatcher
{
    private static readonly string[] AltExtensions = [".flac", ".mp3"];
    private const int MaxFilenameCandidates = 24;
    private const int MaxTagLibProbes = 8;
    private const int DurationToleranceMs = 3000;
    /// <summary>Hard cap for recursive name search under master (UI/MCP safety).</summary>
    private static readonly TimeSpan FilenameSearchBudget = TimeSpan.FromSeconds(5);

    public static MasterMatchResult Find(LibraryTrack? track, string? masterRoot, string? linkedMasterPath = null)
    {
        if (string.IsNullOrWhiteSpace(masterRoot))
        {
            return new MasterMatchResult
            {
                Kind = MasterMatchKind.None,
                Message = "No master library root configured.",
            };
        }

        masterRoot = NormalizeRoot(masterRoot);
        if (!DirectoryExistsSafe(masterRoot, TimeSpan.FromSeconds(3)))
        {
            return new MasterMatchResult
            {
                Kind = MasterMatchKind.Offline,
                Message = $"Master root unreachable from this PC: {masterRoot}",
            };
        }

        // 1) Explicit / stored link
        var link = FirstNonEmpty(linkedMasterPath, track?.MasterPath);
        if (!string.IsNullOrWhiteSpace(link))
        {
            var resolved = TryResolveExisting(link);
            if (resolved is not null)
            {
                return new MasterMatchResult
                {
                    Kind = MasterMatchKind.Linked,
                    Path = resolved,
                    Message = "Using stored master link.",
                };
            }

            // Stale link — fall through to auto match.
        }

        var relative = track?.RelativePath;
        var sonosPath = track?.Path;

        // 2) Same relative path under master
        if (!string.IsNullOrWhiteSpace(relative))
        {
            var direct = CombineUnderRoot(masterRoot, relative);
            if (direct is not null && FileExistsSafe(direct))
            {
                return new MasterMatchResult
                {
                    Kind = MasterMatchKind.RelativePath,
                    Path = direct,
                    Message = "Matched by relative path under master root.",
                };
            }

            // 3) Same relative path, alternate extension
            var alt = TryAlternateExtension(direct ?? CombineUnderRoot(masterRoot, relative), relative, masterRoot);
            if (alt is not null)
            {
                return new MasterMatchResult
                {
                    Kind = MasterMatchKind.RelativePathAltExt,
                    Path = alt,
                    Message = "Matched by relative path with alternate extension.",
                };
            }

            // 4) Trailing path segments (master tree has extra prefix folders)
            var suffix = TryPathSuffix(masterRoot, relative);
            if (suffix is not null)
            {
                return new MasterMatchResult
                {
                    Kind = MasterMatchKind.PathSuffix,
                    Path = suffix,
                    Message = "Matched by trailing path segments under master root.",
                };
            }
        }

        // 5) Filename search under master (scoped first, then budgeted full walk)
        var fileName = !string.IsNullOrWhiteSpace(sonosPath)
            ? Path.GetFileName(sonosPath)
            : !string.IsNullOrWhiteSpace(relative)
                ? Path.GetFileName(relative.Replace('/', '\\'))
                : null;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new MasterMatchResult
            {
                Kind = MasterMatchKind.None,
                Message = "No master twin found (no filename to search).",
            };
        }

        var names = BuildNameSearchList(fileName);
        var hits = new List<string>(MaxFilenameCandidates + 1);
        var timedOut = false;

        // Prefer artist / album folders when present — avoids walking the whole archive.
        foreach (var scope in BuildPreferredSearchRoots(masterRoot, track))
        {
            foreach (var name in names)
            {
                var (scoped, scopeTimeout) = EnumerateFilesBudgeted(
                    scope, name, MaxFilenameCandidates + 1 - hits.Count, FilenameSearchBudget);
                timedOut |= scopeTimeout;
                AddUnique(hits, scoped);
                if (hits.Count > MaxFilenameCandidates)
                    break;
            }
            if (hits.Count > MaxFilenameCandidates)
                break;
        }

        // Full tree only if still empty (or only partial) and budget remains useful.
        if (hits.Count == 0)
        {
            foreach (var name in names)
            {
                var (found, walkTimeout) = EnumerateFilesBudgeted(
                    masterRoot, name, MaxFilenameCandidates + 1, FilenameSearchBudget);
                timedOut |= walkTimeout;
                AddUnique(hits, found);
                if (hits.Count > 0 || timedOut)
                    break;
            }
        }

        if (hits.Count == 0)
        {
            return new MasterMatchResult
            {
                Kind = MasterMatchKind.None,
                Message = timedOut
                    ? "No master twin found (search timed out — link manually or use matching relative paths)."
                    : "No master twin found.",
            };
        }

        if (hits.Count == 1)
        {
            return new MasterMatchResult
            {
                Kind = MasterMatchKind.FilenameUnique,
                Path = hits[0],
                Message = timedOut
                    ? "Matched by unique filename under master (search stopped early)."
                    : "Matched by unique filename under master.",
                Candidates = hits,
            };
        }

        if (hits.Count > MaxFilenameCandidates)
        {
            return new MasterMatchResult
            {
                Kind = MasterMatchKind.Ambiguous,
                Message = $"Too many master files named '{fileName}' (>{MaxFilenameCandidates}); link manually.",
                Candidates = hits.Take(MaxFilenameCandidates).ToList(),
            };
        }

        // 6) Score candidates by path tokens + metadata (TagLib probes capped)
        var scored = ScoreCandidates(hits, track);
        if (scored.Count == 0)
        {
            return new MasterMatchResult
            {
                Kind = MasterMatchKind.Ambiguous,
                Message = "Multiple master candidates; none scored.",
                Candidates = hits,
            };
        }

        var best = scored[0];
        var second = scored.Count > 1 ? scored[1] : (Score: int.MinValue, Path: (string?)null);

        // Require a clear winner
        if (best.Score >= 3 && (second.Path is null || best.Score >= second.Score + 2))
        {
            return new MasterMatchResult
            {
                Kind = MasterMatchKind.Metadata,
                Path = best.Path,
                Message = $"Matched by metadata/path score ({best.Score}).",
                Candidates = scored.Select(s => s.Path!).Take(8).ToList(),
            };
        }

        return new MasterMatchResult
        {
            Kind = MasterMatchKind.Ambiguous,
            Message = "Multiple master candidates; scores too close — link manually.",
            Candidates = scored.Select(s => s.Path!).Take(12).ToList(),
        };
    }

    private static List<(int Score, string Path)> ScoreCandidates(IReadOnlyList<string> hits, LibraryTrack? track)
    {
        var list = new List<(int Score, string Path)>(hits.Count);
        var tagLibLeft = MaxTagLibProbes;
        foreach (var path in hits)
        {
            var score = 0;
            var pathLower = path.ToLowerInvariant();

            void HitToken(string? token, int points)
            {
                if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
                    return;
                if (pathLower.Contains(token.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                    score += points;
            }

            HitToken(track?.Artist, 2);
            HitToken(track?.AlbumArtist, 1);
            HitToken(track?.Album, 2);
            HitToken(track?.Title, 1);

            // Light TagLib probe for duration / track / title agreement (capped).
            if (tagLibLeft > 0)
            {
                tagLibLeft--;
                try
                {
                    using var file = TagLib.File.Create(path);
                    var tag = file.Tag;
                    var props = file.Properties;

                    if (track?.DurationMs is long dMs && props.Duration.TotalMilliseconds > 0)
                    {
                        var delta = Math.Abs(props.Duration.TotalMilliseconds - dMs);
                        if (delta <= DurationToleranceMs)
                            score += 3;
                        else if (delta <= DurationToleranceMs * 2)
                            score += 1;
                        else
                            score -= 1;
                    }

                    if (track?.TrackNumber is int tn && tag.Track > 0 && (int)tag.Track == tn)
                        score += 2;

                    if (!string.IsNullOrWhiteSpace(track?.Title)
                        && !string.IsNullOrWhiteSpace(tag.Title)
                        && string.Equals(track.Title.Trim(), tag.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                        score += 3;

                    if (!string.IsNullOrWhiteSpace(track?.Artist)
                        && !string.IsNullOrWhiteSpace(tag.FirstPerformer)
                        && string.Equals(track.Artist.Trim(), tag.FirstPerformer.Trim(), StringComparison.OrdinalIgnoreCase))
                        score += 2;

                    if (!string.IsNullOrWhiteSpace(track?.Album)
                        && !string.IsNullOrWhiteSpace(tag.Album)
                        && string.Equals(track.Album.Trim(), tag.Album.Trim(), StringComparison.OrdinalIgnoreCase))
                        score += 2;
                }
                catch
                {
                    // Unreadable candidate — keep path-token score only.
                }
            }

            list.Add((score, path));
        }

        return list.OrderByDescending(x => x.Score).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> BuildNameSearchList(string fileName)
    {
        var list = new List<string> { fileName };
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        foreach (var alt in AltExtensions)
        {
            if (string.Equals(alt, ext, StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(baseName + alt);
        }
        return list;
    }

    private static IEnumerable<string> BuildPreferredSearchRoots(string masterRoot, LibraryTrack? track)
    {
        // Artist\Album and Artist only — common master layouts.
        var artist = SanitizeFolderToken(track?.AlbumArtist) ?? SanitizeFolderToken(track?.Artist);
        var album = SanitizeFolderToken(track?.Album);
        if (artist is not null && album is not null)
        {
            var both = Path.Combine(masterRoot, artist, album);
            if (DirectoryExistsSafe(both, TimeSpan.FromSeconds(2)))
                yield return both;
        }
        if (artist is not null)
        {
            var art = Path.Combine(masterRoot, artist);
            if (DirectoryExistsSafe(art, TimeSpan.FromSeconds(2)))
                yield return art;
        }
    }

    private static string? SanitizeFolderToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim();
        // Strip characters invalid in Windows folder names; keep spaces.
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// Enumerate files named <paramref name="searchPattern"/> under <paramref name="root"/>,
    /// stopping after <paramref name="maxHits"/> or <paramref name="budget"/> (hard timeout via task wait).
    /// </summary>
    private static (List<string> Hits, bool TimedOut) EnumerateFilesBudgeted(
        string root, string searchPattern, int maxHits, TimeSpan budget)
    {
        if (maxHits <= 0 || string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(searchPattern))
            return ([], false);

        try
        {
            var task = Task.Run(() =>
            {
                var local = new List<string>(Math.Min(maxHits, 16));
                var sw = Stopwatch.StartNew();
                foreach (var f in Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories))
                {
                    local.Add(f);
                    if (local.Count >= maxHits)
                        break;
                    // Cooperative stop if the walk itself is slow (many dirs).
                    if (sw.Elapsed >= budget)
                        break;
                }
                return local;
            });

            if (!task.Wait(budget))
            {
                AppLog.Warn($"Master filename search timed out under {root} ({searchPattern})");
                return ([], true);
            }

            return (task.Result, task.Result.Count >= maxHits);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Master filename search failed under {root} ({searchPattern})", ex);
            return ([], false);
        }
    }

    private static void AddUnique(List<string> target, IEnumerable<string> add)
    {
        foreach (var p in add)
        {
            if (target.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                continue;
            target.Add(p);
        }
    }

    private static bool DirectoryExistsSafe(string path, TimeSpan timeout)
    {
        try
        {
            // UNC can hang; bound with a short task wait.
            var task = Task.Run(() => Directory.Exists(path));
            if (!task.Wait(timeout))
            {
                AppLog.Warn($"Master path Exists timed out ({timeout.TotalSeconds:0}s): {path}");
                return false;
            }
            return task.Result;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Master path Exists failed: {path}", ex);
            return false;
        }
    }

    private static bool FileExistsSafe(string path)
    {
        try
        {
            var task = Task.Run(() => File.Exists(path));
            return task.Wait(TimeSpan.FromSeconds(2)) && task.Result;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryPathSuffix(string masterRoot, string relative)
    {
        var parts = relative.Replace('/', '\\')
            .Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        // Try longer suffixes first (more specific).
        for (var start = 0; start < parts.Length - 1; start++)
        {
            var suffix = string.Join(Path.DirectorySeparatorChar, parts.Skip(start));
            var candidate = CombineUnderRoot(masterRoot, suffix);
            if (candidate is not null && FileExistsSafe(candidate))
                return candidate;

            var alt = TryAlternateExtension(candidate, suffix, masterRoot);
            if (alt is not null)
                return alt;
        }

        return null;
    }

    private static string? TryAlternateExtension(string? primaryPath, string relativeOrSuffix, string masterRoot)
    {
        var baseRel = Path.ChangeExtension(relativeOrSuffix.Replace('/', '\\'), null);
        if (string.IsNullOrWhiteSpace(baseRel))
            return null;

        var primaryExt = primaryPath is not null
            ? Path.GetExtension(primaryPath)
            : Path.GetExtension(relativeOrSuffix);

        foreach (var ext in AltExtensions)
        {
            if (string.Equals(ext, primaryExt, StringComparison.OrdinalIgnoreCase))
                continue;
            var candidate = CombineUnderRoot(masterRoot, baseRel + ext);
            if (candidate is not null && FileExistsSafe(candidate))
                return candidate;
        }

        return null;
    }

    private static string? CombineUnderRoot(string root, string relative)
    {
        try
        {
            var cleaned = relative.Replace('/', '\\').TrimStart('\\');
            var full = Path.GetFullPath(Path.Combine(root, cleaned));
            // Stay under master root (path traversal guard).
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return full;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveExisting(string path)
    {
        try
        {
            var p = path.Trim();
            if (!FileExistsSafe(p))
                return null;
            try { return Path.GetFullPath(p); }
            catch { return p; }
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeRoot(string root) =>
        root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }
}
