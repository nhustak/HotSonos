using System.Windows.Controls;
using HotSonos.Activity;
using UserControl = System.Windows.Controls.UserControl;

namespace HotSonos.App.Reports;

public partial class MusicReportView : UserControl, IReportPanel
{
    private readonly ActivityLog _log;

    public MusicReportView(ActivityLog log)
    {
        _log = log;
        InitializeComponent();
        FromDate.SelectedDate = DateTime.Today.AddDays(-30);
        ToDate.SelectedDate = DateTime.Today;
    }

    public void RefreshReport() => Load();

    private void Apply_Click(object sender, System.Windows.RoutedEventArgs e) => Load();

    private void Load()
    {
        try
        {
            var report = _log.QueryMusic(FromDate.SelectedDate, ToDate.SelectedDate, SkippedOnly.IsChecked == true, 500);
            PlayGrid.ItemsSource = report.Plays;
            MostPlayedList.ItemsSource = report.MostPlayed;
            MostSkippedList.ItemsSource = report.MostSkipped;
            RecentList.ItemsSource = report.RecentlyPlayed;
            StatusText.Text = report.Plays.Count == 0
                ? "No songs in this range. " + PlayOutcomeClassifier.Summary
                : $"{report.Plays.Count} play(s) shown. {PlayOutcomeClassifier.Summary}";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}
