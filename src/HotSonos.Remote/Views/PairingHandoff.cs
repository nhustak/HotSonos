using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

/// <summary>
/// One path for a scanned code, an opened hotsonos://pair link, a pasted link, and a password pair.
/// A successful read saves the address, port, and PIN and tests the PC. The password is not saved.
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

    public static void NotePasswordNeeded()
    {
        const string message = "Enter the pairing password.";
        if (_visible is not null)
        {
            _visible.Status.Text = message;
            _pendingStatus = null;
            return;
        }

        _pendingStatus = message;
    }

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

    public static async Task<(bool Ok, string Message)> ApplyAsync(string raw, bool alert = true)
    {
        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!PhonePairLink.TryParse(raw, out var host, out var port, out var pin, out var error))
            {
                var message = error ?? "That is not a HotSonos pairing link.";
                Report(message, alert: alert);
                return (false, message);
            }

            return await AcceptCore(host, port, pin, alert).ConfigureAwait(true);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Asks the PC for the house PIN. On success the phone saves that PIN the same way a QR code does.
    /// The password is not written to preferences.
    /// </summary>
    public static async Task<(bool Ok, string Message)> PairWithPasswordAsync(string? hostField, string? portField, string? password)
    {
        if (!RemoteNet.TryParseAddress(hostField, portField, out var host, out var port, out var error))
            return (false, error ?? "Enter the PC address.");
        if (string.IsNullOrWhiteSpace(password))
            return (false, "Enter the pairing password.");

        RemoteCall<RemotePairResponse> call;
        try
        {
            call = await AppServices.Client.PairAsync(host, port, password, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return (false, string.IsNullOrWhiteSpace(ex.Message) ? "Could not pair." : ex.Message);
        }

        if (call.Kind != RemoteCallKind.Ok || call.Value is not { Ok: true, Pin: { } pin } body
            || !RemoteNet.IsSixDigitPin(pin))
            return (false, call.Value?.Error ?? call.Detail ?? "Could not pair.");

        var savedHost = body.Host ?? "";
        var savedPort = body.Port;
        if (!PhonePairLink.TryFormat(savedHost, savedPort, pin, out var link, out _))
        {
            savedHost = host;
            savedPort = port;
            if (!PhonePairLink.TryFormat(savedHost, savedPort, pin, out link, out var linkError))
                return (false, linkError ?? "The PC did not return a PIN.");
        }

        return await ApplyAsync(link, alert: false).ConfigureAwait(true);
    }

    static async Task<(bool Ok, string Message)> AcceptCore(string host, int port, string pin, bool alert)
    {
        Fill(host, port, pin);
        Report("Connecting…", quiet: true, alert: alert);

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
                var missed = call.Detail ?? "Could not reach the PC.";
                Report(missed, alert: alert);
                return (false, missed);
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
                return (true, message);
            }

            Report(message, alert: alert);

            if (AppServices.Link.Preference == PhoneLinkPreference.Standalone)
                AppServices.Link.SetPreference(PhoneLinkPreference.Pc);
            await AppServices.Link.ResolveAsync(CancellationToken.None).ConfigureAwait(true);
            return (true, message);
        }
        catch (Exception ex)
        {
            if (restore)
                AppServices.Client.Apply(oldHost, oldPort, oldPin);
            var message = string.IsNullOrWhiteSpace(ex.Message) ? "Could not connect." : ex.Message;
            Report(message, alert: alert);
            return (false, message);
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

    static void Report(string message, bool quiet = false, bool alert = true)
    {
        if (_visible is not null)
        {
            _visible.Status.Text = message;
            _pendingStatus = null;
            return;
        }

        _pendingStatus = message;
        if (!alert || quiet || Application.Current?.MainPage is PairPage)
            return;
        if (Application.Current?.MainPage is Page page)
            _ = page.DisplayAlert("HotSonos", message, "OK");
    }
}
