using System.Windows.Controls;
using HotSonos.Activity;
using UserControl = System.Windows.Controls.UserControl;

namespace HotSonos.App.Reports;

public interface IReportPanel
{
    void RefreshReport();
}

/// <summary>
/// One entry in the Reports list. Add a report by adding a UserControl and one
/// item here — the host does not know the individual reports.
/// </summary>
public sealed class ReportDescriptor
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Blurb { get; init; }
    public required Func<ActivityLog, UserControl> Create { get; init; }
}

public static class ReportCatalog
{
    public static IReadOnlyList<ReportDescriptor> All { get; } =
    [
        new()
        {
            Id = "song-history",
            Title = "Song Play History",
            Blurb = "Search a song and see every time it played.",
            Create = log => new SongPlayHistoryReport(log),
        },
        new()
        {
            Id = "music",
            Title = "Music",
            Blurb = "Songs in order, with skips, completions, and totals.",
            Create = log => new MusicReportView(log),
        },
        new()
        {
            Id = "events",
            Title = "Event Log",
            Blurb = "Music and Sonos system events.",
            Create = log => new EventLogReport(log),
        },
    ];
}
