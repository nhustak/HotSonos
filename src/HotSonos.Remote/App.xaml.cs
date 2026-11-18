namespace HotSonos.Remote;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        var connection = AppServices.Connection;
        if (connection.IsPaired)
            AppServices.Client.Apply(connection.Host, connection.Port, connection.Pin);
        var open = AppServices.Link.Preference != HotSonos.Remote.Protocol.PhoneLinkPreference.Pc || connection.IsPaired;
        MainPage = open ? new AppShell() : new NavigationPage(new Views.PairPage());
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);
        window.Created += (_, _) => _ = AppServices.Link.ResolveAsync(CancellationToken.None);
        window.Resumed += (_, _) => _ = AppServices.Link.ResolveAsync(CancellationToken.None);
        return window;
    }

    public static void EnterApp()
    {
        var connection = AppServices.Connection;
        if (connection.IsPaired)
            AppServices.Client.Apply(connection.Host, connection.Port, connection.Pin);
        Current!.MainPage = new AppShell();
        _ = AppServices.Link.ResolveAsync(CancellationToken.None);
    }

    public static void LeaveToPair()
    {
        AppServices.Live.Stop();
        Current!.MainPage = new NavigationPage(new Views.PairPage());
    }
}
