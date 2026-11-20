using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using HotSonos.Remote.Protocol;

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
        if (e is not null && e.KeyCode is Keycode.VolumeUp or Keycode.VolumeDown
            && (AppServices.Link.OnPc ? AppServices.Connection.IsPaired : AppServices.Link.Speakers.Zones.Count > 0))
        {
            if (e.Action == KeyEventActions.Down && e.RepeatCount == 0)
            {
                var direction = e.KeyCode == Keycode.VolumeUp ? 1 : -1;
                if (AppServices.Link.OnPc)
                    _ = AppServices.Client.StepVolumeAsync(direction, CancellationToken.None);
                else
                    _ = AppServices.Link.Speakers.StepHouseVolumeAsync(direction * PhoneVolume.SpeakerStep, CancellationToken.None);
            }

            return true;
        }

        return base.DispatchKeyEvent(e);
    }
}
