namespace HotSonos.Remote.Protocol;

/// <summary>
/// Widths for the phone remote on a resizable window.
/// A phone-width window keeps the full-bleed column. Wider windows center that column
/// so buttons do not stretch across a Chromebook desktop.
/// </summary>
public static class RemoteWindow
{
    /// <summary>Player art and transport. Matches a large phone so the existing Player design stays put.</summary>
    public const double PlayerColumnMax = 480;

    /// <summary>Lists and settings. Wide enough to read, narrow enough that a button is not a desktop bar.</summary>
    public const double PageColumnMax = 640;

    /// <summary>Below this, the tab strip scrolls. At and above it, every tab is on screen.</summary>
    public const double TabSpreadMinWidth = 680;

    public const double TabButtonMax = 132;

    const double TabButtonMin = 84;

    /// <summary>
    /// Square album-art side. Callers pass the column width and the viewport height.
    /// The Player uses this value for both width and height and does not shrink it to fit text.
    /// </summary>
    public static double ArtSide(double width, double height)
    {
        var side = Math.Min(width - 48, height * 0.46);
        return Math.Clamp(side, 160, 420);
    }

    public static double Column(double available, double max)
    {
        if (available <= 0 || double.IsNaN(available) || double.IsInfinity(available) || max <= 0)
            return 0;
        return Math.Min(available, max);
    }

    /// <summary>Equal tab width, or -1 to keep each label at its own width and scroll.</summary>
    public static double TabButtonWidth(double width, int count)
    {
        if (count <= 0 || width < TabSpreadMinWidth || double.IsNaN(width))
            return -1;
        var each = Math.Floor((width - 8) / count);
        if (each < TabButtonMin)
            return -1;
        return Math.Min(each, TabButtonMax);
    }
}
