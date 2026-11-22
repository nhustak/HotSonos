using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;

namespace HotSonos.Remote;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if ANDROID
        ViewActivate.Install();
#endif
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().UseBarcodeReader();
#if IOS
        IosPairingLinks.Install(builder);
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
