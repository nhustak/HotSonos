using System.Windows.Controls;
using HotSonos.Activity;
using UserControl = System.Windows.Controls.UserControl;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace HotSonos.App.Reports;

public partial class EventLogReport : UserControl, IReportPanel
{
    private readonly ActivityLog _log;
    private bool _loading = true;

    public EventLogReport(ActivityLog log)
    {
        _log = log;
        InitializeComponent();
        ReloadKinds();
        _loading = false;
    }

    public void RefreshReport() => Load();

    private void Refresh_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ReloadKinds();
        Load();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded)
            return;
        if (sender == CategoryCombo)
            ReloadKinds();
        Load();
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

    private void Load()
    {
        try
        {
            var rows = _log.QueryEvents(SelectedCategory(), SelectedKind(), 500);
            EventGrid.ItemsSource = rows;
            StatusText.Text = rows.Count == 0
                ? "No events in this filter yet."
                : $"{rows.Count} event(s), newest first. The log keeps 180 days, up to 80,000 events.";
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
