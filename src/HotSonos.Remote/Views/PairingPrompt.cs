using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

/// <summary>
/// A later 401 means the saved PIN was rejected. Ask for the pairing password again
/// instead of showing that as an error. The password is not saved.
/// </summary>
static class PairingPrompt
{
    public const string AskAgain = "Enter the pairing password.";

    static int _open;
    static int _snooze;

    /// <summary>Status text for a saved-session 401. Opens the password prompt instead of a PIN error.</summary>
    public static string Miss<T>(RemoteCall<T> call, string fallback)
    {
        if (call.Kind != RemoteCallKind.Unauthorized)
            return string.IsNullOrWhiteSpace(call.Detail) ? fallback : call.Detail;
        Show(fromUser: false);
        return AskAgain;
    }

    public static void Show(bool fromUser)
    {
        if (fromUser)
            Interlocked.Exchange(ref _snooze, 0);
        else if (Volatile.Read(ref _snooze) == 1)
            return;

        MainThread.BeginInvokeOnMainThread(Open);
    }

    public static void Dismissed()
    {
        Interlocked.Exchange(ref _snooze, 1);
        Interlocked.Exchange(ref _open, 0);
    }

    public static void Closed() => Interlocked.Exchange(ref _open, 0);

    static void Open()
    {
        if (Volatile.Read(ref _snooze) == 1)
            return;
        if (Interlocked.CompareExchange(ref _open, 1, 0) != 0)
            return;

        var page = WindowPage();
        if (page is null || page is PairPage)
        {
            Interlocked.Exchange(ref _open, 0);
            if (page is PairPage)
                PairingHandoff.NotePasswordNeeded();
            return;
        }

        try
        {
            var pushing = page.Navigation.PushModalAsync(new PasswordPairPage());
            _ = pushing.ContinueWith(t =>
            {
                if (!t.IsFaulted)
                    return;
                Interlocked.Exchange(ref _open, 0);
                MainThread.BeginInvokeOnMainThread(() => AppShell.OpenConnection());
            }, TaskScheduler.Default);
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _open, 0);
            AppShell.OpenConnection();
        }
    }

    static Page? WindowPage()
    {
        var app = Application.Current;
        if (app is null)
            return null;
        foreach (var window in app.Windows)
        {
            if (window?.Page is Page page)
                return page;
        }

        return app.MainPage;
    }
}
