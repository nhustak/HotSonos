namespace HotSonos.Remote;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        var connection = AppServices.Connection;
        if (connection.IsPaired)
            AppServices.Client.Apply(connection.Host, connection.Port, connection.Pin);
        MainPage = connection.IsPaired ? new AppShell() : new NavigationPage(new Views.PairPage());
    }

    public static void EnterApp()
    {
        var connection = AppServices.Connection;
        AppServices.Client.Apply(connection.Host, connection.Port, connection.Pin);
        Current!.MainPage = new AppShell();
    }

    public static void LeaveToPair()
    {
        AppServices.Live.Stop();
        Current!.MainPage = new NavigationPage(new Views.PairPage());
    }
}
