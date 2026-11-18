using System.Windows;
using System.Windows.Controls;
using HotSonos.Activity;
using UserControl = System.Windows.Controls.UserControl;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace HotSonos.App.Reports;

public partial class SongPlayHistoryReport : UserControl, IReportPanel
{
    private const int PageSize = 500;
    private readonly ActivityLog _log;
    private readonly List<PlaySessionRow> _plays = [];
    private int _playOffset;

    public SongPlayHistoryReport(ActivityLog log)
    {
        _log = log;
        InitializeComponent();
        HintText.Text = PlayOutcomeClassifier.Summary;
    }

    public void RefreshReport() => Search();

    private void Search_Click(object sender, RoutedEventArgs e) => Search();

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
                _plays.Clear();
                PlayGrid.ItemsSource = null;
                PlaysHeader.Text = "Plays";
                MorePlaysButton.Visibility = Visibility.Collapsed;
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
        _playOffset = 0;
        _plays.Clear();
        LoadPlays(song);
    }

    private void MorePlays_Click(object sender, RoutedEventArgs e)
    {
        if (SongGrid.SelectedItem is SongStatRow song)
            LoadPlays(song);
    }

    private void LoadPlays(SongStatRow song)
    {
        try
        {
            var plays = _log.PlaysForSong(song.TrackKey, PageSize, _playOffset);
            _plays.AddRange(plays);
            _playOffset = _plays.Count;
            PlayGrid.ItemsSource = null;
            PlayGrid.ItemsSource = _plays.ToList();
            var who = string.IsNullOrWhiteSpace(song.Artist) ? song.Title : $"{song.Title} — {song.Artist}";
            PlaysHeader.Text = _plays.Count < song.PlayCount
                ? $"{who}  ·  {song.PlayCount} plays, {song.SkipCount} skips  ·  showing {_plays.Count}"
                : $"{who}  ·  {song.PlayCount} plays, {song.SkipCount} skips";
            MorePlaysButton.Visibility = plays.Count >= PageSize ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}
