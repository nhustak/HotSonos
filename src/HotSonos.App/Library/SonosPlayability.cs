namespace HotSonos.App.Library;

/// <summary>
/// Heuristic playability against Sonos <b>local Music Library</b> limits
/// (S2-era: mono/stereo, ≤48 kHz, FLAC/ALAC ≤24-bit, MP3 ≤320 kbps).
/// Spec: https://support.sonos.com/en/article/supported-audio-formats-for-sonos-music-library
/// Not a guarantee — speakers may still skip for other reasons.
/// </summary>
public static class SonosPlayability
{
    public const int MaxSampleRateHz = 48_000;
    public const int MaxLosslessBits = 24;
    public const int MaxMp3BitrateKbps = 320;

    public sealed record Result(bool Playable, string? Issue);

    public static Result Evaluate(
        string? codec,
        string? extension,
        int? sampleRateHz,
        int? bitsPerSample,
        int? channels,
        int? bitrateKbps)
    {
        var ext = (extension ?? "").TrimStart('.').ToLowerInvariant();
        var codecNorm = (codec ?? "").Trim().ToLowerInvariant();

        if (channels is > 2)
            return new(false, $"channels={channels} (Sonos library: mono/stereo only)");

        if (sampleRateHz is > MaxSampleRateHz)
            return new(false, $"sample rate {sampleRateHz} Hz > {MaxSampleRateHz} Hz");

        var isFlac = ext is "flac" || codecNorm.Contains("flac", StringComparison.Ordinal);
        var isAlac = ext is "m4a" or "alac" || codecNorm.Contains("alac", StringComparison.Ordinal);
        var isMp3 = ext is "mp3" || codecNorm.Contains("mpeg", StringComparison.Ordinal) || codecNorm.Contains("mp3", StringComparison.Ordinal);
        var isAiff = ext is "aiff" or "aif";
        var isWav = ext is "wav" or "wave";

        if (isFlac || isAlac)
        {
            if (bitsPerSample is > MaxLosslessBits)
                return new(false, $"bit depth {bitsPerSample} > {MaxLosslessBits}-bit (FLAC/ALAC)");
        }
        else if (isAiff)
        {
            if (bitsPerSample is > 16)
                return new(false, $"AIFF bit depth {bitsPerSample} > 16-bit");
        }
        else if (isMp3)
        {
            if (bitrateKbps is > MaxMp3BitrateKbps)
                return new(false, $"MP3 bitrate {bitrateKbps} kbps > {MaxMp3BitrateKbps}");
        }
        else if (isWav)
        {
            // WAV often works at CD/48k stereo; flag obvious hi-res.
            if (bitsPerSample is > 24)
                return new(false, $"WAV bit depth {bitsPerSample} likely unsupported");
        }
        else if (!string.IsNullOrEmpty(ext) && ext is not "ogg" and not "wma" and not "aac" and not "mp4")
        {
            // Indexed but unusual extension — leave unknown playable=true with note only when clear fail.
        }

        // Unknown props: still "playable" optimistically so we don't mass-flag incomplete scans.
        if (sampleRateHz is null && bitsPerSample is null && bitrateKbps is null)
            return new(true, "audio props unknown — re-scan recommended");

        return new(true, null);
    }

    public static string FormatLabel(LibraryTrack t)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.Codec)) parts.Add(t.Codec!);
        if (t.BitsPerSample is int b) parts.Add($"{b}-bit");
        if (t.SampleRateHz is int sr)
            parts.Add(sr >= 1000 ? $"{sr / 1000.0:0.###} kHz" : $"{sr} Hz");
        if (t.BitrateKbps is int br) parts.Add($"{br} kbps");
        if (t.Channels is int ch) parts.Add(ch == 1 ? "mono" : ch == 2 ? "stereo" : $"{ch}ch");
        return parts.Count == 0 ? "—" : string.Join(" / ", parts);
    }
}
