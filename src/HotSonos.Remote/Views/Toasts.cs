namespace HotSonos.Remote.Views;

/// <summary>Short result line on the shell. Errors use this too, so a failed call is not silent.</summary>
static class Toasts
{
    public static void Show(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Application.Current?.MainPage is AppShell shell)
                shell.ShowToast(text.Trim());
        });
    }
}
