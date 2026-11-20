using Microsoft.Extensions.Logging;

namespace HotSonos.Remote;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if ANDROID
        ViewActivate.Install();
#endif
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
