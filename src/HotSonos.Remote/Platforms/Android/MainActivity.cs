using Android.App;
using Android.Content.PM;
using Android.Views;
using Android.Widget;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ResizeableActivity = true,
    ScreenOrientation = ScreenOrientation.Unspecified,
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.Orientation
        | ConfigChanges.Keyboard
        | ConfigChanges.KeyboardHidden
        | ConfigChanges.Navigation
        | ConfigChanges.UiMode
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    static readonly SemaphoreSlim VolumeGate = new(1, 1);
    static long _lastKeyVolume;

    /// <summary>Hardware volume keys step the house volume, same as the previous remote.</summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is null)
            return base.DispatchKeyEvent(e);

        if (e.KeyCode is Keycode.VolumeUp or Keycode.VolumeDown && VolumeReady())
        {
            if (e.Action == KeyEventActions.Down && e.RepeatCount == 0)
                _ = StepFromKeyAsync(e.KeyCode == Keycode.VolumeUp ? 1 : -1);
            return true;
        }

        if (Token(e.KeyCode) is { } token
            && RemoteShortcuts.TryMatch(token, Shift(e), Typing(), Chord(e), out var shortcut))
        {
            if (!RemoteCommands.CanControl())
                return base.DispatchKeyEvent(e);

            if (e.Action == KeyEventActions.Down)
                Run(shortcut, e.RepeatCount);
            return true;
        }

        return base.DispatchKeyEvent(e);
    }

    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        var handled = base.DispatchGenericMotionEvent(e);
        if (handled || e is null)
            return handled;
        return MouseWheel.TryScroll(this, e);
    }

    static void Run(RemoteShortcut shortcut, int repeat)
    {
        switch (shortcut)
        {
            case RemoteShortcut.PlayPause when repeat == 0:
                _ = RemoteCommands.PlayPauseAsync();
                break;
            case RemoteShortcut.Previous when repeat == 0:
                _ = RemoteCommands.PreviousAsync();
                break;
            case RemoteShortcut.Next when repeat == 0:
                _ = RemoteCommands.NextAsync();
                break;
            case RemoteShortcut.Mute when repeat == 0:
                _ = RemoteCommands.MuteAsync();
                break;
            case RemoteShortcut.VolumeUp when VolumeDue(repeat):
                _ = StepFromKeyAsync(1);
                break;
            case RemoteShortcut.VolumeDown when VolumeDue(repeat):
                _ = StepFromKeyAsync(-1);
                break;
        }
    }

    /// <summary>Same step as a hardware volume key. Holding a keyboard key repeats at the on-screen button rate.</summary>
    static bool VolumeDue(int repeat)
    {
        var now = Environment.TickCount64;
        if (repeat != 0 && now - _lastKeyVolume < 160)
            return false;
        _lastKeyVolume = now;
        return true;
    }

    static bool VolumeReady() =>
        AppServices.Link.OnPc
            ? AppServices.Connection.IsPaired
            : AppServices.Link.Speakers.Zones.Count > 0;

    bool Typing() => CurrentFocus is EditText;

    static bool Shift(KeyEvent e) =>
        (e.MetaState & (MetaKeyStates.ShiftOn | MetaKeyStates.ShiftLeftOn | MetaKeyStates.ShiftRightOn)) != 0;

    static bool Chord(KeyEvent e) =>
        (e.MetaState & (MetaKeyStates.CtrlOn | MetaKeyStates.AltOn | MetaKeyStates.MetaOn)) != 0;

    static string? Token(Keycode code) => code switch
    {
        Keycode.Space => "Space",
        Keycode.DpadLeft => "Left",
        Keycode.DpadRight => "Right",
        Keycode.DpadUp => "Up",
        Keycode.DpadDown => "Down",
        Keycode.Plus or Keycode.NumpadAdd => "Plus",
        Keycode.Minus or Keycode.NumpadSubtract => "Minus",
        Keycode.Equals => "Equals",
        Keycode.M => "M",
        Keycode.MediaPlayPause or Keycode.MediaPlay or Keycode.MediaPause or Keycode.Headsethook => "MediaPlayPause",
        Keycode.MediaNext or Keycode.MediaFastForward => "MediaNext",
        Keycode.MediaPrevious or Keycode.MediaRewind => "MediaPrevious",
        Keycode.VolumeMute or Keycode.Mute => "MediaMute",
        _ => null,
    };

    static async Task StepFromKeyAsync(int direction)
    {
        await VolumeGate.WaitAsync().ConfigureAwait(false);
        try
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
        finally
        {
            VolumeGate.Release();
        }
    }
}
