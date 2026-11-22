using Foundation;
using Microsoft.Maui.LifecycleEvents;
using UIKit;

namespace HotSonos.Remote;

/// <summary>
/// Opens hotsonos://pair on iPhone and iPad. Scene-based iOS delivers the link
/// to the scene, and older launches deliver it to the application delegate.
/// </summary>
static class IosPairingLinks
{
    public static void Install(MauiAppBuilder builder)
    {
        builder.ConfigureLifecycleEvents(events =>
        {
            events.AddiOS(ios =>
            {
                ios.OpenUrl((application, url, options) =>
                {
                    Views.PairingHandoff.Offer(url.AbsoluteString);
                    return true;
                });
                ios.SceneOpenUrl((scene, contexts) =>
                {
                    Offer(contexts);
                    return true;
                });
                ios.FinishedLaunching((_, options) =>
                {
                    if (options is not null && options[UIApplication.LaunchOptionsUrlKey] is NSUrl url)
                        Views.PairingHandoff.Offer(url.AbsoluteString);
                    return true;
                });
                ios.SceneWillConnect((_, _, options) => Offer(options?.UrlContexts));
            });
        });
    }

    static void Offer(NSSet<UIOpenUrlContext>? contexts)
    {
        if (contexts is null)
            return;
        foreach (UIOpenUrlContext context in contexts)
            Views.PairingHandoff.Offer(context.Url?.AbsoluteString);
    }
}
