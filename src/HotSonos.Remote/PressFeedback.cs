namespace HotSonos.Remote;

/// <summary>One short tick. A device with no vibrator still runs the button.</summary>
static class Haptics
{
    public static void Tick()
    {
        try
        {
            if (HapticFeedback.Default.IsSupported)
                HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch (Exception)
        {
            // Missing vibrator hardware is not a failed action.
        }
    }
}

/// <summary>
/// Pressed state for every MAUI button. Android already dims with native alpha in
/// <c>ButtonFeedback</c>. Other targets dim the MAUI opacity. The tick is immediate.
/// </summary>
static class PressFeedback
{
    static readonly BindableProperty WiredProperty = BindableProperty.CreateAttached(
        "PressWired",
        typeof(bool),
        typeof(PressFeedback),
        false);

    public static void Install()
    {
        Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("HotSonosPress", (_, view) =>
        {
            if (view is not Button button)
                return;
            if (button.GetValue(WiredProperty) is true)
                return;
            button.SetValue(WiredProperty, true);
            button.Pressed += (_, _) =>
            {
                Haptics.Tick();
#if !ANDROID
                button.Opacity = 0.72;
#endif
            };
            button.Released += (_, _) =>
            {
#if !ANDROID
                if (button.IsEnabled)
                    button.Opacity = 1;
#endif
            };
        });
    }
}
