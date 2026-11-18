using System.Windows;
using System.Windows.Controls;
using HotSonos.Activity;
using UserControl = System.Windows.Controls.UserControl;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace HotSonos.App.Reports;

public partial class EventLogReport : UserControl, IReportPanel
{
    private const int PageSize = 500;
    private readonly ActivityLog _log;
    private readonly List<ActivityEventRow> _rows = [];
    private bool _loading = true;
    private int _offset;

    public EventLogReport(ActivityLog log)
    {
        _log = log;
        InitializeComponent();
        ReloadKinds();
        _loading = false;
    }

    public void RefreshReport() => Load(append: false);

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ReloadKinds();
        Load(append: false);
    }

    private void More_Click(object sender, RoutedEventArgs e) => Load(append: true);

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded)
            return;
        if (sender == CategoryCombo)
            ReloadKinds();
        Load(append: false);
    }

    private void ReloadKinds()
    {
        var selected = (KindCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _loading = true;
        KindCombo.Items.Clear();
        KindCombo.Items.Add(new ComboBoxItem { Content = "All", Tag = "" });
        foreach (var kind in _log.EventKinds(SelectedCategory()))
            KindCombo.Items.Add(new ComboBoxItem { Content = kind.Replace('_', ' '), Tag = kind });

        var match = KindCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, selected, StringComparison.Ordinal));
        KindCombo.SelectedItem = match ?? KindCombo.Items[0];
        _loading = false;
    }

    private void Load(bool append)
    {
        try
        {
            if (!append)
            {
                _offset = 0;
                _rows.Clear();
            }

            var rows = _log.QueryEvents(SelectedCategory(), SelectedKind(), PageSize, _offset);
            _rows.AddRange(rows);
            _offset = _rows.Count;
            EventGrid.ItemsSource = null;
            EventGrid.ItemsSource = _rows.ToList();
            MoreButton.Visibility = rows.Count >= PageSize ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _rows.Count == 0
                ? "No events in this filter yet."
                : $"{_rows.Count} event(s), newest first. The log keeps 180 days, up to 80,000 events.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private string? SelectedCategory()
    {
        var tag = (CategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.IsNullOrEmpty(tag) ? null : tag;
    }

    private string? SelectedKind()
    {
        var tag = (KindCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.IsNullOrEmpty(tag) ? null : tag;
    }
}
