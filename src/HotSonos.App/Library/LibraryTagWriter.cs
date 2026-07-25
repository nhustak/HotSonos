using System.IO;
using HotSonos.App.Infrastructure;

namespace HotSonos.App.Library;

/// <summary>Optional fields to write into an audio file (null = leave unchanged).</summary>
public sealed class TrackTagUpdate
{
    /// <summary>
    /// Full replacement set of opaque catalog keys for <c>HOTSONOS_TAGS</c>.
    /// Null = leave unchanged; empty list = clear all HotSonos tags on the file.
    /// </summary>
    public IReadOnlyList<string>? TagKeys { get; init; }

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public int? TrackNumber { get; init; }
    public int? Year { get; init; }
    public double? Bpm { get; init; }

    public bool HasAnyChange =>
        TagKeys is not null
        || Title is not null
        || Artist is not null
        || Album is not null
        || Genre is not null
        || TrackNumber is not null
        || Year is not null
        || Bpm is not null;
}

public sealed class TagWriteResult
{
    public required bool Ok { get; init; }
    public required string Path { get; init; }
    public bool DryRun { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Changes { get; init; } = [];
    public LibraryTrack? TrackAfter { get; init; }

    /// <summary>True when write was deferred because the file was locked (e.g. Sonos playing it).</summary>
    public bool Queued { get; init; }

    /// <summary>True when failure is a share/lock violation (caller may queue).</summary>
    public bool FileLocked { get; init; }

    // ---- Master dual-write (step 4) ----------------------------------------
    public bool UpdateMasterRequested { get; init; }
    public string? MasterPath { get; init; }
    public string? MasterMatchKind { get; init; }
    public string? MasterMessage { get; init; }
    public IReadOnlyList<string> MasterChanges { get; init; } = [];
    public string? MasterError { get; init; }
    public bool MasterWritten { get; init; }
    public IReadOnlyList<string> MasterCandidates { get; init; } = [];
}

/// <summary>Result of stripping one tag key from every matching track in the library cache.</summary>
public sealed class TagPurgeResult
{
    public bool Ok { get; init; }
    public int Matched { get; init; }
    public int Written { get; init; }
    public int Queued { get; init; }
    public int Failed { get; init; }
    public string Message { get; init; } = "";
    public string? LastError { get; init; }
}

/// <summary>
/// Writes tags into FLAC (Vorbis / Xiph) and MP3 (ID3v2) without re-encoding audio.
/// HotSonos field: <see cref="LibraryTagReader.TagsField"/> = semicolon-separated opaque keys.
/// </summary>
public static class LibraryTagWriter
{
    public static TagWriteResult Write(string fullPath, TrackTagUpdate update, bool dryRun, string? rootForRescan)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return Fail(fullPath, "path is required");
        if (!update.HasAnyChange)
            return Fail(fullPath, "No tag fields provided.");

        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return Fail(fullPath, "File not found.");

        var ext = info.Extension;
        if (!LibraryTagReader.AudioExtensions.Contains(ext))
            return Fail(fullPath, $"Unsupported extension '{ext}' (FLAC/MP3 only).");

        string? tagsNorm = null;
        if (update.TagKeys is not null)
            tagsNorm = LibraryTagReader.JoinTagKeys(update.TagKeys);

        try
        {
            var changes = new List<string>();
            // Share-aware open + short retries: Sonos often holds the playing file open for read.
            using var file = OpenTagLibFileWithRetry(fullPath);
            var tag = file.Tag;

            if (update.Title is not null)
            {
                var v = NullIfEmpty(update.Title);
                if (!string.Equals(tag.Title, v, StringComparison.Ordinal))
                {
                    changes.Add($"title → {v ?? "(clear)"}");
                    tag.Title = v ?? "";
                }
            }

            if (update.Artist is not null)
            {
                var v = NullIfEmpty(update.Artist);
                var cur = tag.FirstPerformer;
                if (!string.Equals(cur, v, StringComparison.Ordinal))
                {
                    changes.Add($"artist → {v ?? "(clear)"}");
                    tag.Performers = v is null ? [] : [v];
                }
            }

            if (update.Album is not null)
            {
                var v = NullIfEmpty(update.Album);
                if (!string.Equals(tag.Album, v, StringComparison.Ordinal))
                {
                    changes.Add($"album → {v ?? "(clear)"}");
                    tag.Album = v ?? "";
                }
            }

            if (update.Genre is not null)
            {
                var v = NullIfEmpty(update.Genre);
                var cur = tag.FirstGenre;
                if (!string.Equals(cur, v, StringComparison.Ordinal))
                {
                    changes.Add($"genre → {v ?? "(clear)"}");
                    tag.Genres = v is null ? [] : [v];
                }
            }

            if (update.TrackNumber is not null)
            {
                var n = Math.Max(0, update.TrackNumber.Value);
                if (tag.Track != (uint)n)
                {
                    changes.Add($"track → {n}");
                    tag.Track = (uint)n;
                }
            }

            if (update.Year is not null)
            {
                var y = Math.Clamp(update.Year.Value, 0, 9999);
                if (tag.Year != (uint)y)
                {
                    changes.Add($"year → {y}");
                    tag.Year = (uint)y;
                }
            }

            if (update.Bpm is not null)
            {
                var bpm = (uint)Math.Clamp(Math.Round(update.Bpm.Value), 0, 999);
                if (tag.BeatsPerMinute != bpm)
                {
                    changes.Add($"bpm → {bpm}");
                    tag.BeatsPerMinute = bpm;
                }
            }

            if (update.TagKeys is not null)
            {
                var cur = ReadCustomField(file, LibraryTagReader.TagsField);
                var next = tagsNorm; // null means clear
                // Normalize cur for compare
                var curJoined = LibraryTagReader.JoinTagKeys(LibraryTagReader.ParseTagKeys(cur));
                if (!string.Equals(curJoined, next, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add($"{LibraryTagReader.TagsField} → {next ?? "(clear)"}");
                    WriteCustomField(file, LibraryTagReader.TagsField, next);
                }

                // Strip legacy HOTSONOS_TEMPO whenever we touch tags (tempo is gone).
                var legacyTempo = ReadCustomField(file, LibraryTagReader.LegacyTempoField);
                if (!string.IsNullOrWhiteSpace(legacyTempo))
                {
                    changes.Add($"{LibraryTagReader.LegacyTempoField} → (clear)");
                    WriteCustomField(file, LibraryTagReader.LegacyTempoField, null);
                }
            }

            if (changes.Count == 0)
            {
                return new TagWriteResult
                {
                    Ok = true,
                    Path = info.FullName,
                    DryRun = dryRun,
                    Message = "No changes (values already match).",
                    Changes = [],
                    TrackAfter = rootForRescan is not null
                        ? LibraryTagReader.TryRead(info.FullName, rootForRescan, DateTime.UtcNow)
                        : null,
                };
            }

            if (dryRun)
            {
                return new TagWriteResult
                {
                    Ok = true,
                    Path = info.FullName,
                    DryRun = true,
                    Message = "Dry run — file not modified.",
                    Changes = changes,
                };
            }

            file.Save();
            AppLog.Info($"Tags written: {info.FullName} ({string.Join("; ", changes)})");

            LibraryTrack? after = null;
            if (rootForRescan is not null)
                after = LibraryTagReader.TryRead(info.FullName, rootForRescan, DateTime.UtcNow);

            return new TagWriteResult
            {
                Ok = true,
                Path = info.FullName,
                DryRun = false,
                Message = "Tags saved.",
                Changes = changes,
                TrackAfter = after,
            };
        }
        catch (Exception ex) when (IsFileLockException(ex))
        {
            AppLog.Warn($"Tag write locked (will queue if requested): {fullPath}", ex);
            return new TagWriteResult
            {
                Ok = false,
                Path = fullPath,
                Error = ex.Message,
                Message = ex.Message,
                FileLocked = true,
            };
        }
        catch (Exception ex)
        {
            AppLog.Error($"Tag write failed: {fullPath}", ex);
            return Fail(fullPath, ex.Message);
        }
    }

    /// <summary>
    /// Open via TagLib with <see cref="FileShare.ReadWrite"/> so a reader (Sonos/SMB)
    /// does not always block tag updates. Retries briefly on share violations.
    /// </summary>
    private static TagLib.File OpenTagLibFileWithRetry(string fullPath)
    {
        const int attempts = 4;
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                var abs = new ShareAwareFileAbstraction(fullPath);
                return TagLib.File.Create(abs);
            }
            catch (Exception ex) when (IsFileLockException(ex))
            {
                last = ex;
                Thread.Sleep(80 * (i + 1));
            }
        }

        throw last ?? new IOException($"Could not open for tag write: {fullPath}");
    }

    internal static bool IsFileLockException(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is IOException)
            {
                var hr = e.HResult & 0xFFFF;
                // ERROR_SHARING_VIOLATION=32, ERROR_LOCK_VIOLATION=33
                if (hr is 32 or 33)
                    return true;
                if (e.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains("cannot access the file", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>TagLib abstraction that opens streams with share-read/write (not exclusive).</summary>
    private sealed class ShareAwareFileAbstraction : TagLib.File.IFileAbstraction
    {
        private readonly string _path;

        public ShareAwareFileAbstraction(string path) => _path = path;

        public string Name => _path;

        public Stream ReadStream => OpenStream();

        public Stream WriteStream => OpenStream();

        public void CloseStream(Stream stream) => stream.Dispose();

        private Stream OpenStream() =>
            new FileStream(
                _path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.RandomAccess);
    }

    private static string? ReadCustomField(TagLib.File file, string field)
    {
        try
        {
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                var values = xiph.GetField(field);
                if (values is { Length: > 0 } && !string.IsNullOrWhiteSpace(values[0]))
                    return values[0].Trim();
            }

            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    if (string.Equals(frame.Description, field, StringComparison.OrdinalIgnoreCase)
                        && frame.Text is { Length: > 0 }
                        && !string.IsNullOrWhiteSpace(frame.Text[0]))
                    {
                        return frame.Text[0].Trim();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Read custom field {field} before write failed", ex);
        }

        return null;
    }

    private static void WriteCustomField(TagLib.File file, string field, string? valueOrNull)
    {
        // FLAC / Ogg Vorbis comments
        var xiph = file.GetTag(TagLib.TagTypes.Xiph, create: true) as TagLib.Ogg.XiphComment;
        if (xiph is not null)
        {
            if (valueOrNull is null)
                xiph.RemoveField(field);
            else
                xiph.SetField(field, valueOrNull);
        }

        // MP3 ID3v2 TXXX
        var id3 = file.GetTag(TagLib.TagTypes.Id3v2, create: true) as TagLib.Id3v2.Tag;
        if (id3 is not null)
        {
            var toRemove = id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
                .Where(f => string.Equals(f.Description, field, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var f in toRemove)
                id3.RemoveFrame(f);

            if (valueOrNull is not null)
            {
                var frame = new TagLib.Id3v2.UserTextInformationFrame(field)
                {
                    Text = [valueOrNull],
                };
                id3.AddFrame(frame);
            }
        }
    }

    private static TagWriteResult Fail(string path, string error) => new()
    {
        Ok = false,
        Path = path,
        Error = error,
        Message = error,
    };

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
