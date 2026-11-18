using System.Windows;
using System.Windows.Controls;
using HotSonos.Activity;
using UserControl = System.Windows.Controls.UserControl;

namespace HotSonos.App.Reports;

public partial class MusicReportView : UserControl, IReportPanel
{
    private const int PageSize = 500;
    private readonly ActivityLog _log;
    private readonly List<PlaySessionRow> _plays = [];
    private int _offset;

    public MusicReportView(ActivityLog log)
    {
        _log = log;
        InitializeComponent();
        FromDate.SelectedDate = DateTime.Today.AddDays(-30);
        ToDate.SelectedDate = DateTime.Today;
    }

    public void RefreshReport() => Load(append: false);

    private void Apply_Click(object sender, RoutedEventArgs e) => Load(append: false);

    private void More_Click(object sender, RoutedEventArgs e) => Load(append: true);

    private void Load(bool append)
    {
        try
        {
            if (!append)
            {
                _offset = 0;
                _plays.Clear();
            }

            var report = _log.QueryMusic(
                FromDate.SelectedDate, ToDate.SelectedDate, SkippedOnly.IsChecked == true, PageSize, _offset);
            _plays.AddRange(report.Plays);
            _offset = _plays.Count;
            PlayGrid.ItemsSource = null;
            PlayGrid.ItemsSource = _plays.ToList();
            if (!append)
            {
                MostPlayedList.ItemsSource = report.MostPlayed;
                MostSkippedList.ItemsSource = report.MostSkipped;
                RecentList.ItemsSource = report.RecentlyPlayed;
            }

            MoreButton.Visibility = report.Plays.Count >= PageSize ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _plays.Count == 0
                ? "No songs in this range. " + PlayOutcomeClassifier.Summary
                : $"{_plays.Count} play(s) shown. {PlayOutcomeClassifier.Summary}";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}
