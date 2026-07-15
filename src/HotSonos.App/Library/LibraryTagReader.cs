using System.IO;
using HotSonos.App.Infrastructure;

namespace HotSonos.App.Library;

/// <summary>Reads tags + audio properties + optional HOTSONOS_TEMPO from FLAC/MP3 via TagLib#.</summary>
public static class LibraryTagReader
{
    public const string TempoField = "HOTSONOS_TEMPO";

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

            var tempo = ReadCustomTempo(file);

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
                Tempo = tempo,
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

    private static string? ReadCustomTempo(TagLib.File file)
    {
        try
        {
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                var values = xiph.GetField(TempoField);
                if (values is { Length: > 0 } && !string.IsNullOrWhiteSpace(values[0]))
                    return NormalizeTempo(values[0]);
            }

            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    if (string.Equals(frame.Description, TempoField, StringComparison.OrdinalIgnoreCase)
                        && frame.Text is { Length: > 0 }
                        && !string.IsNullOrWhiteSpace(frame.Text[0]))
                    {
                        return NormalizeTempo(frame.Text[0]);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("HOTSONOS_TEMPO read failed", ex);
        }

        return null;
    }

    private static string? NormalizeTempo(string raw)
    {
        var t = raw.Trim().ToLowerInvariant();
        return t is "slow" or "medium" or "fast" ? t : raw.Trim();
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
