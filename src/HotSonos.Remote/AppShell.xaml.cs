namespace HotSonos.Remote;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        AppServices.Live.Start();
    }
}
