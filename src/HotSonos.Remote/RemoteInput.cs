using HotSonos.Remote.Views;

namespace HotSonos.Remote;

/// <summary>
/// Keyboard activation for views that are not buttons (icon transport, section headers).
/// Android installs a click/hover/key path. Other targets get a tap.
/// </summary>
static class RemoteInput
{
    public static Action<View, Func<Task>>? Activate { get; set; }

    /// <summary>Platform long-press / right-click. Null uses a pointer hold.</summary>
    public static Action<View, Func<Task>>? Hold { get; set; }

    public static Action<VisualElement>? SkipTab { get; set; }

    public static void MakeActivable(View view, Func<Task> activate)
    {
        var run = Busy(view, activate);
        if (Activate is not null)
        {
            Activate(view, run);
            return;
        }

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            Haptics.Tick();
            try
            {
                await run().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Toasts.Show(ex.Message);
            }
        };
        view.GestureRecognizers.Add(tap);
    }

    static Func<Task> Busy(View view, Func<Task> activate)
    {
        var gate = 0;
        return async () =>
        {
            if (Interlocked.Exchange(ref gate, 1) == 1)
                return;
            var opacity = view.Opacity;
            try
            {
                view.Opacity = 0.55;
                view.IsEnabled = false;
                await activate().ConfigureAwait(true);
            }
            finally
            {
                view.IsEnabled = true;
                view.Opacity = opacity <= 0 ? 1 : opacity;
                Interlocked.Exchange(ref gate, 0);
            }
        };
    }

    /// <summary>Long-press (and a right-click on a Chromebook) runs <paramref name="hold"/> once.</summary>
    public static void MakeHoldable(View view, Func<Task> hold)
    {
        if (Hold is not null)
        {
            Hold(view, hold);
            return;
        }

        var token = 0;
        var pointer = new PointerGestureRecognizer();
        pointer.PointerPressed += (_, _) =>
        {
            var mine = ++token;
            _ = ArmAsync(mine);
        };
        pointer.PointerReleased += (_, _) => token++;
        pointer.PointerExited += (_, _) => token++;
        view.GestureRecognizers.Add(pointer);

        async Task ArmAsync(int mine)
        {
            await Task.Delay(500).ConfigureAwait(true);
            if (mine != token)
                return;
            token++;
            try
            {
                await hold().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (Application.Current?.MainPage is Page page)
                    await page.DisplayAlert("HotSonos", ex.Message, "OK");
            }
        }
    }

    public static void OmitFromTabOrder(VisualElement view) => SkipTab?.Invoke(view);
}
