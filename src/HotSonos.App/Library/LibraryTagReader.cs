using System.IO;
using HotSonos.App.Infrastructure;

namespace HotSonos.App.Library;

/// <summary>Reads tags + audio properties + <c>HOTSONOS_TAGS</c> keys from FLAC/MP3 via TagLib#.</summary>
public static class LibraryTagReader
{
    /// <summary>Single multi-value field: opaque catalog keys, semicolon-separated.</summary>
    public const string TagsField = "HOTSONOS_TAGS";

    /// <summary>Legacy field — never used for tags; cleared when rewriting HOTSONOS_TAGS.</summary>
    public const string LegacyTempoField = "HOTSONOS_TEMPO";

    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3",
    };

    public static LibraryTrack? TryRead(string fullPath, string root, DateTime scannedUtc)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
                return null;

            using var file = TagLib.File.Create(fullPath);
            var tag = file.Tag;
            var props = file.Properties;

            double? bpm = null;
            if (tag.BeatsPerMinute > 0)
                bpm = tag.BeatsPerMinute;

            var tagKeys = ReadTagKeys(file);

            string? relative = null;
            try
            {
                relative = System.IO.Path.GetRelativePath(root, fullPath);
                if (relative.StartsWith("..", StringComparison.Ordinal))
                    relative = null;
            }
            catch
            {
                relative = null;
            }

            var codec = DescribeCodec(props, info.Extension);
            int? sampleRate = props.AudioSampleRate > 0 ? props.AudioSampleRate : null;
            int? bits = props.BitsPerSample > 0 ? props.BitsPerSample : null;
            int? channels = props.AudioChannels > 0 ? props.AudioChannels : null;
            int? bitrateKbps = props.AudioBitrate > 0 ? props.AudioBitrate : null;

            // FLAC sometimes reports 0 bits in TagLib depending on stream; try description.
            if (bits is null or 0 && props.Description is { } desc)
            {
                var m = System.Text.RegularExpressions.Regex.Match(desc, @"(\d+)\s*bit", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed) && parsed is > 0 and <= 64)
                    bits = parsed;
            }

            var play = SonosPlayability.Evaluate(codec, info.Extension, sampleRate, bits, channels, bitrateKbps);

            return new LibraryTrack
            {
                Path = info.FullName,
                Root = root,
                RelativePath = relative,
                Title = NullIfEmpty(tag.Title),
                Artist = NullIfEmpty(tag.FirstPerformer) ?? NullIfEmpty(string.Join("; ", tag.Performers)),
                Album = NullIfEmpty(tag.Album),
                AlbumArtist = NullIfEmpty(tag.FirstAlbumArtist) ?? NullIfEmpty(string.Join("; ", tag.AlbumArtists)),
                Genre = NullIfEmpty(tag.FirstGenre) ?? NullIfEmpty(string.Join("; ", tag.Genres)),
                TrackNumber = tag.Track > 0 ? (int)tag.Track : null,
                Year = tag.Year > 0 ? (int)tag.Year : null,
                DurationMs = props.Duration.TotalMilliseconds > 0
                    ? (long)props.Duration.TotalMilliseconds
                    : null,
                TagKeys = tagKeys,
                Bpm = bpm,
                Codec = codec,
                SampleRateHz = sampleRate,
                BitsPerSample = bits is 0 ? null : bits,
                Channels = channels,
                BitrateKbps = bitrateKbps,
                SonosPlayable = play.Playable,
                SonosPlayIssue = play.Issue,
                FileSize = info.Length,
                FileMtimeUtc = info.LastWriteTimeUtc,
                LastScannedUtc = scannedUtc,
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Library tag read failed: {fullPath}", ex);
            return null;
        }
    }

    private static string? DescribeCodec(TagLib.Properties props, string extension)
    {
        if (!string.IsNullOrWhiteSpace(props.Description))
            return props.Description.Trim();
        var codecs = props.Codecs?.Where(c => c is not null).Select(c => c!.Description).Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        if (codecs is { Count: > 0 })
            return string.Join(", ", codecs!);
        var ext = extension.TrimStart('.').ToUpperInvariant();
        return string.IsNullOrEmpty(ext) ? null : ext;
    }

    /// <summary>Parse <see cref="TagsField"/> into ordered unique keys.</summary>
    public static List<string> ReadTagKeys(TagLib.File file)
    {
        var raw = ReadCustomField(file, TagsField);
        return ParseTagKeys(raw);
    }

    public static List<string> ParseTagKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        return raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? JoinTagKeys(IEnumerable<string> keys)
    {
        var list = keys
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count == 0 ? null : string.Join(';', list);
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
                    if (frame.Description is null
                        || !string.Equals(frame.Description, field, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (frame.Text is { Length: > 0 } && !string.IsNullOrWhiteSpace(frame.Text[0]))
                        return frame.Text[0].Trim();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Read {field} failed", ex);
        }

        return null;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
