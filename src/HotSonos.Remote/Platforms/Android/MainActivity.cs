using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace HotSonos.Remote;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.KeyboardHidden)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>Hardware volume keys step the house volume, same as the previous remote.</summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (AppServices.Connection.IsPaired && e is not null
            && e.KeyCode is Keycode.VolumeUp or Keycode.VolumeDown)
        {
            if (e.Action == KeyEventActions.Down && e.RepeatCount == 0)
            {
                var direction = e.KeyCode == Keycode.VolumeUp ? 1 : -1;
                _ = AppServices.Client.StepVolumeAsync(direction, CancellationToken.None);
            }

            return true;
        }

        return base.DispatchKeyEvent(e);
    }
}
