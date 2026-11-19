using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        var connection = AppServices.Connection;
        if (connection.IsPaired)
            AppServices.Client.Apply(connection.Host, connection.Port, connection.Pin);
        MainPage = PhoneLaunch.OpenConnectScreen(AppServices.Link.Preference, connection.IsPaired)
            ? new Views.PairPage()
            : new AppShell();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);
        window.Created += (_, _) => ResumeLink();
        window.Resumed += (_, _) => ResumeLink();
        return window;
    }

    static void ResumeLink()
    {
        if (Current?.MainPage is AppShell)
            _ = AppServices.Link.ResolveAsync(CancellationToken.None);
    }

    public static void EnterApp()
    {
        var connection = AppServices.Connection;
        if (connection.IsPaired)
            AppServices.Client.Apply(connection.Host, connection.Port, connection.Pin);
        Current!.MainPage = new AppShell();
        _ = AppServices.Link.ResolveAsync(CancellationToken.None);
    }

    public static void ShowConnect()
    {
        AppServices.Live.Stop();
        Current!.MainPage = new Views.PairPage();
    }
}
