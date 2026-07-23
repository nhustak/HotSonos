using System.IO;
using HotSonos.App.Infrastructure;

namespace HotSonos.App.Library;

/// <summary>Optional fields to write into an audio file (null = leave unchanged).</summary>
public sealed class TrackTagUpdate
{
    /// <summary><c>slow</c> | <c>medium</c> | <c>fast</c>, or empty string to clear.</summary>
    public string? Tempo { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public int? TrackNumber { get; init; }
    public int? Year { get; init; }
    public double? Bpm { get; init; }

    public bool HasAnyChange =>
        Tempo is not null
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

/// <summary>
/// Writes tags into FLAC (Vorbis / Xiph) and MP3 (ID3v2) without re-encoding audio.
/// Custom field: <see cref="LibraryTagReader.TempoField"/> = HOTSONOS_TEMPO.
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

        string? tempoNorm = null;
        if (update.Tempo is not null)
        {
            if (!IsValidTempoOrClear(update.Tempo, out tempoNorm, out var tempoErr))
                return Fail(fullPath, tempoErr!);
        }

        try
        {
            var changes = new List<string>();
            using var file = TagLib.File.Create(fullPath);
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

            if (update.Tempo is not null)
            {
                var cur = ReadTempo(file);
                var next = tempoNorm; // null means clear
                if (!string.Equals(cur, next, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add($"HOTSONOS_TEMPO → {next ?? "(clear)"}");
                    WriteTempo(file, next);
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
        catch (Exception ex)
        {
            AppLog.Error($"Tag write failed: {fullPath}", ex);
            return Fail(fullPath, ex.Message);
        }
    }

    private static bool IsValidTempoOrClear(string raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalized = null; // clear
            return true;
        }

        var t = raw.Trim().ToLowerInvariant();
        if (t is "slow" or "medium" or "fast")
        {
            normalized = t;
            return true;
        }

        error = "tempo must be slow, medium, fast, or empty to clear.";
        return false;
    }

    private static string? ReadTempo(TagLib.File file)
    {
        try
        {
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                var values = xiph.GetField(LibraryTagReader.TempoField);
                if (values is { Length: > 0 } && !string.IsNullOrWhiteSpace(values[0]))
                    return values[0].Trim().ToLowerInvariant();
            }

            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    if (string.Equals(frame.Description, LibraryTagReader.TempoField, StringComparison.OrdinalIgnoreCase)
                        && frame.Text is { Length: > 0 }
                        && !string.IsNullOrWhiteSpace(frame.Text[0]))
                    {
                        return frame.Text[0].Trim().ToLowerInvariant();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Read tempo before write failed", ex);
        }

        return null;
    }

    private static void WriteTempo(TagLib.File file, string? tempoOrNull)
    {
        // FLAC / Ogg Vorbis comments
        var xiph = file.GetTag(TagLib.TagTypes.Xiph, create: true) as TagLib.Ogg.XiphComment;
        if (xiph is not null)
        {
            if (tempoOrNull is null)
                xiph.RemoveField(LibraryTagReader.TempoField);
            else
                xiph.SetField(LibraryTagReader.TempoField, tempoOrNull);
        }

        // MP3 ID3v2 TXXX
        var id3 = file.GetTag(TagLib.TagTypes.Id3v2, create: true) as TagLib.Id3v2.Tag;
        if (id3 is not null)
        {
            // Remove existing HOTSONOS_TEMPO user text frames
            var toRemove = id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
                .Where(f => string.Equals(f.Description, LibraryTagReader.TempoField, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var f in toRemove)
                id3.RemoveFrame(f);

            if (tempoOrNull is not null)
            {
                var frame = new TagLib.Id3v2.UserTextInformationFrame(LibraryTagReader.TempoField)
                {
                    Text = [tempoOrNull],
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
