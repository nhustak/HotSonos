namespace HotSonos.Remote;

/// <summary>
/// Keyboard activation for views that are not buttons (icon transport, section headers).
/// Android installs a click/hover/key path. Other targets get a tap.
/// </summary>
static class RemoteInput
{
    public static Action<View, Func<Task>>? Activate { get; set; }

    public static Action<VisualElement>? SkipTab { get; set; }

    public static void MakeActivable(View view, Func<Task> activate)
    {
        if (Activate is not null)
        {
            Activate(view, activate);
            return;
        }

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try
            {
                await activate().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (Application.Current?.MainPage is Page page)
                    await page.DisplayAlert("HotSonos", ex.Message, "OK");
            }
        };
        view.GestureRecognizers.Add(tap);
    }

    public static void OmitFromTabOrder(VisualElement view) => SkipTab?.Invoke(view);
}
