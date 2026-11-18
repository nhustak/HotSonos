using System.Windows.Controls;
using HotSonos.Activity;
using UserControl = System.Windows.Controls.UserControl;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace HotSonos.App.Reports;

public partial class SongPlayHistoryReport : UserControl, IReportPanel
{
    private readonly ActivityLog _log;

    public SongPlayHistoryReport(ActivityLog log)
    {
        _log = log;
        InitializeComponent();
        HintText.Text = PlayOutcomeClassifier.Summary;
    }

    public void RefreshReport() => Search();

    private void Search_Click(object sender, System.Windows.RoutedEventArgs e) => Search();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        Search();
        e.Handled = true;
    }

    private void Search()
    {
        try
        {
            var rows = _log.SearchSongs(SearchBox.Text, 200);
            SongGrid.ItemsSource = rows;
            StatusText.Text = rows.Count == 0
                ? "No matching songs yet. Plays show up after a song ends."
                : $"{rows.Count} song(s). Select one to see each play.";
            if (rows.Count > 0)
                SongGrid.SelectedIndex = 0;
            else
            {
                PlayGrid.ItemsSource = null;
                PlaysHeader.Text = "Plays";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void SongGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SongGrid.SelectedItem is not SongStatRow song)
            return;
        try
        {
            var plays = _log.PlaysForSong(song.TrackKey, 500);
            PlayGrid.ItemsSource = plays;
            var who = string.IsNullOrWhiteSpace(song.Artist) ? song.Title : $"{song.Title} — {song.Artist}";
            PlaysHeader.Text = $"{who}  ·  {song.PlayCount} plays, {song.SkipCount} skips";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}
