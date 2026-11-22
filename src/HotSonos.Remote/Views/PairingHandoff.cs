using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

/// <summary>
/// One path for a scanned code, an opened hotsonos://pair link, and a pasted link.
/// A successful read saves the address, port, and PIN and tests the PC.
/// </summary>
static class PairingHandoff
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static string? _recent;
    static long _recentAt;
    static ConnectionForm? _visible;
    static (string Host, int Port, string Pin)? _pendingFields;
    static string? _pendingStatus;

    public static void NoteShown(ConnectionForm form)
    {
        _visible = form;
        if (_pendingFields is { } fields)
        {
            form.ApplyFields(fields.Host, fields.Port, fields.Pin);
            _pendingFields = null;
        }

        if (_pendingStatus is { } status)
        {
            form.Status.Text = status;
            _pendingStatus = null;
        }
    }

    public static void NoteHidden(ConnectionForm form)
    {
        if (ReferenceEquals(_visible, form))
            _visible = null;
    }

    public static void DetachVisible() => _visible = null;

    public static void Offer(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return;

        var now = Environment.TickCount64;
        if (string.Equals(text, _recent, StringComparison.Ordinal) && now - _recentAt < 2000)
            return;
        _recent = text;
        _recentAt = now;
        MainThread.BeginInvokeOnMainThread(() => _ = ApplyAsync(text));
    }

    public static async Task ApplyAsync(string raw)
    {
        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!PhonePairLink.TryParse(raw, out var host, out var port, out var pin, out var error))
            {
                Report(error ?? "That is not a HotSonos pairing link.");
                return;
            }

            Fill(host, port, pin);
            Report("Connecting…", quiet: true);

            var previous = AppServices.Connection;
            var restore = previous.IsPaired;
            var oldHost = previous.Host;
            var oldPort = previous.Port;
            var oldPin = previous.Pin;
            try
            {
                AppServices.Client.Apply(host, port, pin);
                var call = await AppServices.Client.GetStatusAsync(CancellationToken.None).ConfigureAwait(true);
                if (call.Kind != RemoteCallKind.Ok)
                {
                    if (restore)
                        AppServices.Client.Apply(oldHost, oldPort, oldPin);
                    Report(call.Detail ?? "Could not reach the PC.");
                    return;
                }

                previous.Host = host;
                previous.Port = port;
                previous.Pin = pin;
                previous.Save();

                var title = call.Value?.NowPlaying?.Title ?? call.Value?.ActiveGroupLabel ?? "Nothing playing yet.";
                var message = "Connected to " + host + ". " + title;
                var entering = Application.Current?.MainPage is PairPage;
                if (entering)
                {
                    _pendingStatus = null;
                    AppServices.Link.SetPreference(PhoneLinkPreference.Pc);
                    App.EnterApp();
                    return;
                }

                Report(message);

                if (AppServices.Link.Preference == PhoneLinkPreference.Standalone)
                    AppServices.Link.SetPreference(PhoneLinkPreference.Pc);
                await AppServices.Link.ResolveAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (restore)
                    AppServices.Client.Apply(oldHost, oldPort, oldPin);
                Report(string.IsNullOrWhiteSpace(ex.Message) ? "Could not connect." : ex.Message);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    static void Fill(string host, int port, string pin)
    {
        if (_visible is not null)
        {
            _visible.ApplyFields(host, port, pin);
            _pendingFields = null;
            return;
        }

        _pendingFields = (host, port, pin);
    }

    static void Report(string message, bool quiet = false)
    {
        if (_visible is not null)
        {
            _visible.Status.Text = message;
            _pendingStatus = null;
            return;
        }

        _pendingStatus = message;
        if (quiet || Application.Current?.MainPage is PairPage)
            return;
        if (Application.Current?.MainPage is Page page)
            _ = page.DisplayAlert("HotSonos", message, "OK");
    }
}
