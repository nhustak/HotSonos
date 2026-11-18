using System.Windows.Controls;
using HotSonos.Activity;
using UserControl = System.Windows.Controls.UserControl;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace HotSonos.App.Reports;

public partial class ReportsHost : UserControl
{
    private readonly Dictionary<string, UserControl> _views = new(StringComparer.Ordinal);
    private ActivityLog? _log;

    public ReportsHost()
    {
        InitializeComponent();
    }

    public void Bind(ActivityLog log)
    {
        _log = log;
        StorePathText.Text = string.IsNullOrWhiteSpace(log.DatabasePath)
            ? "Activity log is unavailable."
            : log.DatabasePath;
        ReportList.ItemsSource = ReportCatalog.All;
        if (ReportList.SelectedIndex < 0 && ReportCatalog.All.Count > 0)
            ReportList.SelectedIndex = 0;
    }

    public void RefreshActive()
    {
        if (ReportContent.Content is IReportPanel panel)
            panel.RefreshReport();
    }

    private void ReportList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_log is null || ReportList.SelectedItem is not ReportDescriptor descriptor)
            return;
        if (!_views.TryGetValue(descriptor.Id, out var view))
        {
            view = descriptor.Create(_log);
            _views[descriptor.Id] = view;
        }

        ReportContent.Content = view;
        if (view is IReportPanel panel)
            panel.RefreshReport();
    }
}
