using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotSonos.Remote.Protocol;

/// <summary>
/// Messages between the Wear OS companion and the phone over the Wearable Data Layer.
/// Both sides use <see cref="Path"/>. The watch sends a command string. The phone
/// answers with JSON from <see cref="EncodeState"/>. The watch has no PIN and no speaker address.
/// </summary>
public static class WearWire
{
    public const string Path = "/hotsonos/v1";

    public const string Play = "play";
    public const string Pause = "pause";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string VolumeUp = "volume-up";
    public const string VolumeDown = "volume-down";
    public const string Refresh = "refresh";

    /// <summary>Volume used when the phone has no sample yet and the watch steps.</summary>
    public const int UnknownVolume = 20;

    /// <summary>
    /// How long a watch volume step stays ahead of a slower poll before the poll wins.
    /// </summary>
    public const int VolumeHoldMs = 3000;

    public static string? ParseCommand(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        var token = payload.Trim().ToLowerInvariant();
        return token is Play or Pause or Next or Previous or VolumeUp or VolumeDown or Refresh
            ? token
            : null;
    }

    /// <summary>
    /// One crown or button step. <paramref name="direction"/> is negative for down, positive for up.
    /// Zero keeps the current level. A step smaller than 1 still moves by 1.
    /// </summary>
    public static int StepVolume(int current, int direction, int step)
    {
        var clamped = Math.Clamp(current, 0, MediaControlMapping.VolumeMax);
        if (direction == 0)
            return clamped;
        var size = step < 1 ? 1 : step;
        var delta = direction < 0 ? -size : size;
        return Math.Clamp(clamped + delta, 0, MediaControlMapping.VolumeMax);
    }

    /// <summary>
    /// Keeps a local volume target while the speaker poll is still catching up.
    /// Returns null when there is nothing to hold, when the poll has caught the target,
    /// or when <paramref name="ageMs"/> has reached <see cref="VolumeHoldMs"/>.
    /// </summary>
    public static int? ReconcileTarget(int? target, int? polled, long ageMs)
    {
        if (target is not int held)
            return null;
        if (polled is int seen && seen == held)
            return null;
        if (ageMs >= VolumeHoldMs)
            return null;
        return held;
    }

    public static string EncodeState(MediaControlState state, int? volumeOverride = null)
    {
        int? volume = volumeOverride ?? (state.HasVolume ? state.VolumePercent : null);
        var dto = new WearStateDto(
            state.Title,
            state.Artist,
            state.Playback == MediaControlPlayback.Playing,
            volume);
        return JsonSerializer.Serialize(dto, Json);
    }

    public static WearStateDto? DecodeState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var dto = JsonSerializer.Deserialize<WearStateDto>(json, Json);
            return string.IsNullOrEmpty(dto?.Title) ? null : dto;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>Now-playing snapshot the phone sends to the watch. <see cref="Volume"/> is 0–100, or null when unknown.</summary>
public sealed record WearStateDto(string? Title, string? Artist, bool Playing, int? Volume);
