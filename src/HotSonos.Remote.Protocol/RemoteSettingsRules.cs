namespace HotSonos.Remote.Protocol;

/// <summary>Range checks for settings the phone is allowed to change. The PIN and MCP stay on the PC.</summary>
public static class RemoteSettingsRules
{
    public static bool IsShuffleSource(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        var t = token.Trim();
        if (t.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) && t.Length > "folder:".Length)
            return true;
        if (t.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) && t.Length > "tag:".Length)
            return true;
        if (t.StartsWith("genre:", StringComparison.OrdinalIgnoreCase) && t.Length > "genre:".Length)
            return true;
        return false;
    }

    public static string CanonicalWakeSource(string? source)
    {
        if (string.Equals(source?.Trim(), "Favorite", StringComparison.OrdinalIgnoreCase))
            return "Favorite";
        return "Shuffle";
    }

    /// <summary>Null when every set field is in range. Unset fields are left alone.</summary>
    public static string? Validate(RemoteSettingsBody body)
    {
        if (Out(body.VolumeStep, 1, 50))
            return "Volume step must be 1 to 50.";
        if (Out(body.LevelVolumePercent, 0, 100))
            return "Level volume must be 0 to 100.";
        if (Out(body.NightlyResetMinutes, 0, 1439))
            return "Nightly time must be minutes since midnight, 0 to 1439.";
        if (Out(body.WakeMinutes, 0, 1439))
            return "Wake time must be minutes since midnight, 0 to 1439.";
        if (Out(body.WakeDaysMask, 0, 127))
            return "Wake days must be a Sunday-based bitmask, 0 to 127.";
        if (Out(body.WakeStartVolume, 0, 100) || Out(body.WakeEndVolume, 0, 100))
            return "Wake volume must be 0 to 100.";
        if (Out(body.WakeVolumeStep, 1, 100))
            return "Wake volume step must be 1 to 100.";
        if (Out(body.WakeStepIntervalMinutes, 1, 120))
            return "Wake interval must be 1 to 120 minutes.";
        if (body.WakeSource is not null
            && !body.WakeSource.Trim().Equals("Shuffle", StringComparison.OrdinalIgnoreCase)
            && !body.WakeSource.Trim().Equals("Favorite", StringComparison.OrdinalIgnoreCase))
            return "Wake source must be Shuffle or Favorite.";
        if (body.WakeFavoriteName is { Length: > 200 })
            return "Wake favorite name is too long.";
        if (body.WakeRoom is { Length: > 80 })
            return "Wake room name is too long.";
        if (body.PreferredCoordinator is { Length: > 80 })
            return "Coordinator room name is too long.";
        if (Out(body.ShuffleQueueTracks, 20, 500))
            return "Shuffle queue size must be 20 to 500.";
        if (Out(body.ShuffleTopUpTracks, 10, 300))
            return "Shuffle top-up size must be 10 to 300.";
        if (Out(body.ShuffleHistoryDays, 1, 90))
            return "Shuffle history must be 1 to 90 days.";
        if (Out(body.ShuffleTopUpWhenRemaining, 1, 30))
            return "Shuffle top-up threshold must be 1 to 30.";
        if (body.ControlShuffleSource is not null && !IsShuffleSource(body.ControlShuffleSource))
            return "Shuffle source must be all, folder:path, tag:key, or genre:name.";
        return null;
    }

    private static bool Out(int? value, int min, int max) =>
        value is int n && (n < min || n > max);
}
