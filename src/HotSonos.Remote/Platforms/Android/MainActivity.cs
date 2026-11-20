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
                _ = StepFromKeyAsync(e.KeyCode == Keycode.VolumeUp ? 1 : -1);

            return true;
        }

        return base.DispatchKeyEvent(e);
    }

    static async Task StepFromKeyAsync(int direction)
    {
        MainThread.BeginInvokeOnMainThread(() => PhoneVolumeKeys.Note?.Invoke(direction));
        try
        {
            if (AppServices.Link.OnPc)
                await AppServices.Client.StepVolumeAsync(direction, CancellationToken.None).ConfigureAwait(false);
            else
                await AppServices.Link.Speakers.StepHouseVolumeAsync(direction * PhoneVolume.SpeakerStep, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The Player re-reads below and shows whatever the house still is.
        }

        MainThread.BeginInvokeOnMainThread(() => PhoneVolumeKeys.Refresh?.Invoke());
    }
}
