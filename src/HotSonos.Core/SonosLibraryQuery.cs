namespace HotSonos.Core;

/// <summary>
/// Pure helpers for talking to a speaker's own Music Library index (ContentDirectory A:).
/// </summary>
public static class SonosLibraryQuery
{
    /// <summary>
    /// UPnP Search criteria for a free-text query against title, artist, and album.
    /// Quotes and backslashes are stripped so the criteria stays one phrase.
    /// </summary>
    public static string Criteria(string query)
    {
        var safe = (query ?? "").Replace("\\", "", StringComparison.Ordinal).Replace("\"", "", StringComparison.Ordinal).Trim();
        return $"dc:title contains \"{safe}\" or dc:creator contains \"{safe}\" or upnp:album contains \"{safe}\"";
    }

    /// <summary>
    /// Starting indexes for a few browse windows spread through a large A:TRACKS list.
    /// Each start is in <c>0 … total-window</c>. Returns fewer than <paramref name="windows"/>
    /// when the library is smaller than that.
    /// </summary>
    public static IReadOnlyList<int> SampleStarts(int total, int window, int windows, Random random)
    {
        if (total <= 0 || window <= 0 || windows <= 0)
            return [];

        window = Math.Min(window, total);
        var maxStart = Math.Max(0, total - window);
        var chosen = new HashSet<int>();
        var guard = 0;
        while (chosen.Count < windows && chosen.Count <= maxStart && guard < windows * 20)
        {
            guard++;
            chosen.Add(random.Next(0, maxStart + 1));
        }

        return chosen.OrderBy(i => i).ToList();
    }

    /// <summary>Sonos Seek REL_TIME, <c>H:MM:SS</c>.</summary>
    public static string FormatRelTime(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        var hours = (int)position.TotalHours;
        return $"{hours}:{position.Minutes:00}:{position.Seconds:00}";
    }
}
