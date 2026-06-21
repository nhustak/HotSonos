using System.Windows;
using System.Windows.Threading;

namespace HotSonos.App.Windows;

/// <summary>
/// A small, non-activating, auto-dismissing toast shown bottom-right of the work
/// area. One instance is reused; each message restarts the dismiss timer.
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;

    public ToastWindow()
    {
        InitializeComponent();
        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
        _dismissTimer.Tick += (_, _) => { _dismissTimer.Stop(); Hide(); };
    }

    /// <summary>Shows <paramref name="message"/> and restarts the auto-dismiss timer.</summary>
    public void ShowMessage(string message)
    {
        MessageText.Text = message;
        _dismissTimer.Stop();

        if (!IsVisible)
            Show();
        PositionBottomRight();

        _dismissTimer.Start();
    }

    private void PositionBottomRight()
    {
        // SizeToContent means we must measure before placing.
        UpdateLayout();
        var work = SystemParameters.WorkArea;
        const double margin = 24;
        Left = work.Right - ActualWidth - margin;
        Top = work.Bottom - ActualHeight - margin;
    }

    /// <summary>Prevents Alt+F4 / explicit close from destroying the reusable instance.</summary>
    public void HardClose()
    {
        _dismissTimer.Stop();
        Close();
    }
}
