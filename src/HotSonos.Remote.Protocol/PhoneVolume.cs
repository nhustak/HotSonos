namespace HotSonos.Remote.Protocol;

/// <summary>
/// House-volume steps shared by the phone's hardware volume keys and the Player − / + buttons.
/// Speakers mode has no settings document, so it always uses <see cref="SpeakerStep"/>.
/// PC mode uses the tray's configured volume step, the same value Volume Up / Volume Down apply.
/// </summary>
public static class PhoneVolume
{
    /// <summary>Speakers-mode step. The hardware volume keys pass this to <c>StepHouseVolumeAsync</c>.</summary>
    public const int SpeakerStep = 2;

    public static int ConfiguredStep(bool drivePc, int? pcVolumeStep) =>
        drivePc ? Normalize(pcVolumeStep) : SpeakerStep;

    public static int Normalize(int? step) => step is >= 1 and <= 50 ? step.Value : SpeakerStep;

    /// <summary>Next house percent. <paramref name="direction"/> is negative for down and positive for up.</summary>
    public static int Next(int current, int direction, int step)
    {
        var delta = direction < 0 ? -Normalize(step) : Normalize(step);
        return Math.Clamp(current + delta, 0, 100);
    }

    /// <summary>
    /// Player line between − and +. A missing percent is not shown as the bare word "Volume":
    /// the status cache is empty until something samples, and the label waits on that read.
    /// </summary>
    public static string Label(int? percent, bool muted)
    {
        if (percent is not int value)
            return "Volume …";
        var n = Math.Clamp(value, 0, 100);
        return muted ? $"Volume {n}% · muted" : $"Volume {n}%";
    }
}
