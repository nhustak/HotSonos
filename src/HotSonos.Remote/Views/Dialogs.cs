namespace HotSonos.Remote.Views;

/// <summary>
/// Alerts for tab pages. <see cref="AppShell"/> lifts each tab body out of its page, so that
/// page has no window and never receives NavigatedTo. MAUI then completes DisplayAlert as
/// Cancel without showing it, or leaves the task unfinished. Either way a confirm returns
/// before the action and the request is never sent. Dialogs go to the page that owns the window.
/// </summary>
static class Dialogs
{
    public static Page Host(Page caller)
    {
        if (caller.Window is not null)
            return caller;

        var app = Application.Current;
        if (app is null)
            return caller;

        foreach (var window in app.Windows)
        {
            if (window?.Page is Page page)
                return page;
        }

        return app.MainPage ?? caller;
    }

    public static Task<bool> Confirm(Page caller, string title, string message) =>
        Host(caller).DisplayAlert(title, message, "Do it", "Cancel");

    public static Task Alert(Page caller, string title, string message) =>
        Host(caller).DisplayAlert(title, message, "OK");

    public static Task<string> Sheet(Page caller, string title, string cancel, string? destruction, params string[] buttons) =>
        Host(caller).DisplayActionSheet(title, cancel, destruction, buttons);
}
