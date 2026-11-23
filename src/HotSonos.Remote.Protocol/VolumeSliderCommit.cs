namespace HotSonos.Remote.Protocol;

/// <summary>
/// Tracks a 0–100 house-volume slider so the last thumb value is always flushed.
/// MAUI's Android slider often skips <c>DragCompleted</c> when the thumb hits 0 or 100,
/// so a drag must update the target on every move and flush on a trailing timer,
/// not only on the completed event.
/// </summary>
public sealed class VolumeSliderCommit
{
    /// <summary>Wait after the last move during a drag. Lift or a tap flushes immediately.</summary>
    public const int DragFlushMs = 100;

    public VolumeSliderCommit(int initial = 20)
    {
        var n = Math.Clamp(initial, 0, 100);
        Shown = Target = Applied = n;
    }

    public bool Dragging { get; private set; }
    public int Shown { get; private set; }
    public int Target { get; private set; }
    public int Applied { get; private set; }

    public bool NeedsFlush => Applied != Target;

    public static int Percent(double value) => PhoneVolume.FromSlider(value);

    public void BeginDrag() => Dragging = true;

    /// <summary>
    /// The user moved the thumb or tapped the track.
    /// Returns milliseconds to wait before flushing; 0 means flush now.
    /// </summary>
    public int PointerValue(double value)
    {
        var percent = Percent(value);
        Shown = percent;
        Target = percent;
        return Dragging ? DragFlushMs : 0;
    }

    public int EndDrag(double value)
    {
        Dragging = false;
        var percent = Percent(value);
        Shown = percent;
        Target = percent;
        return 0;
    }

    /// <summary>Page hid or the gesture cancelled. Keep the last target and flush now.</summary>
    public int AbortDrag()
    {
        Dragging = false;
        return 0;
    }

    public void Show(int percent) => Shown = Math.Clamp(percent, 0, 100);

    public void Aim(int percent)
    {
        var n = Math.Clamp(percent, 0, 100);
        Shown = Target = n;
    }

    public void Sync(int percent)
    {
        var n = Math.Clamp(percent, 0, 100);
        Shown = Target = Applied = n;
    }

    public void AppliedOk(int percent) => Applied = Math.Clamp(percent, 0, 100);

    public void Revert()
    {
        Target = Applied;
        Shown = Applied;
    }
}
