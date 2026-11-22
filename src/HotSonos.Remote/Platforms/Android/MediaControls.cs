using Android.Content;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>
/// Keeps the media session aligned with the 2-second now-playing poll.
/// The Settings switch turns it off. Android 13+ is asked for notification permission;
/// the session still starts if that prompt is denied, because the watch reads the
/// foreground media notification, which the system shows for a media playback service.
/// </summary>
static class MediaControls
{
    static readonly HttpClient ArtHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    static readonly SemaphoreSlim Transport = new(1, 1);
    static readonly object VolumeGate = new();

    static bool _installed;
    static bool _asked;
    static int _artGeneration;
    static string? _artToken;
    static byte[]? _artBytes;
    static int _volumePending = int.MinValue;
    static int _volumeRunning;

    public static bool IsEnabled =>
        MediaControlMapping.PreferenceEnabled(
            Preferences.ContainsKey(MediaControlMapping.PreferenceKey)
                ? Preferences.Get(MediaControlMapping.PreferenceKey, true)
                : null);

    public static void Install()
    {
        if (!_installed)
        {
            _installed = true;
            AppServices.Live.Changed += OnChanged;
        }

        EnsureRunning(askPermission: true);
        OnChanged();
    }

    public static void OnHostResumed()
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Page is not AppShell)
            return;
        if (!_installed)
            Install();
        else
            EnsureRunning(askPermission: !_asked);
    }

    public static void SetEnabled(bool enabled)
    {
        Preferences.Set(MediaControlMapping.PreferenceKey, enabled);
        if (enabled)
        {
            EnsureRunning(askPermission: true);
            OnChanged();
            return;
        }

        _artGeneration++;
        _artToken = null;
        _artBytes = null;
        MediaControlsBridge.Clear();
        Stop();
    }

    public static void RequestTransport(MediaTransportRequest request) =>
        _ = DispatchTransportAsync(request);

    public static void RequestVolume(int percent)
    {
        Volatile.Write(ref _volumePending, MediaControlMapping.ClampVolume(percent));
        if (Interlocked.CompareExchange(ref _volumeRunning, 1, 0) != 0)
            return;
        _ = DrainVolumeAsync();
    }

    static void EnsureRunning(bool askPermission)
    {
        if (!IsEnabled)
            return;
        if (askPermission)
            _ = AskPermissionAsync();

        var context = Platform.AppContext;
        if (context is null)
            return;
        try
        {
            var intent = new Intent(context, typeof(MediaControlsService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("HotSonos", "Media controls did not start: " + ex.Message);
        }
    }

    static void Stop()
    {
        var context = Platform.AppContext;
        if (context is null)
            return;
        try
        {
            context.StopService(new Intent(context, typeof(MediaControlsService)));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("HotSonos", "Media controls did not stop: " + ex.Message);
        }
    }

    static async Task AskPermissionAsync()
    {
        if (_asked)
            return;
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>().ConfigureAwait(true);
            if (status == PermissionStatus.Granted)
            {
                _asked = true;
                return;
            }

            // The page can be built before the activity is ready. Resume asks again.
            if (Platform.CurrentActivity is null)
                return;
            _asked = true;
            await Permissions.RequestAsync<Permissions.PostNotifications>().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Older than Android 13, or a Chromebook that will not show the prompt.
            // The player and the foreground media notification still run.
        }
    }

    static void OnChanged()
    {
        if (!IsEnabled)
            return;

        var state = MediaControlMapping.FromStatus(AppServices.Live.Status);
        var token = state.ShowArt ? state.ArtToken : null;
        if (token is null)
        {
            _artToken = null;
            _artBytes = null;
            _artGeneration++;
            MediaControlsBridge.Publish(state, null);
            return;
        }

        if (token == _artToken)
        {
            MediaControlsBridge.Publish(state, _artBytes);
            return;
        }

        MediaControlsBridge.Publish(state, null);
        var generation = ++_artGeneration;
        _ = LoadArtAsync(token, generation);
    }

    static async Task LoadArtAsync(string token, int generation)
    {
        byte[]? bytes = null;
        try
        {
            if (MediaControlMapping.IsDirectArtUri(token))
                bytes = await DownloadArtAsync(token).ConfigureAwait(false);
            else if (AppServices.Link.DrivePc)
            {
                var call = await AppServices.Client.GetArtAsync(CancellationToken.None).ConfigureAwait(false);
                bytes = call.Value;
            }
        }
        catch (Exception)
        {
            return;
        }

        if (bytes is not { Length: > 0 and <= MediaControlMapping.MaxArtBytes })
            return;

        void Apply()
        {
            if (generation != _artGeneration)
                return;
            var fresh = MediaControlMapping.FromStatus(AppServices.Live.Status);
            if (!fresh.ShowArt || fresh.ArtToken != token)
                return;
            _artToken = token;
            _artBytes = bytes;
            MediaControlsBridge.Publish(fresh, bytes);
        }

        if (MainThread.IsMainThread)
            Apply();
        else
            MainThread.BeginInvokeOnMainThread(Apply);
    }

    static async Task<byte[]?> DownloadArtAsync(string uri)
    {
        using var response = await ArtHttp.GetAsync(uri).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        if (response.Content.Headers.ContentLength is long length && length > MediaControlMapping.MaxArtBytes)
            return null;
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return bytes.Length == 0 || bytes.Length > MediaControlMapping.MaxArtBytes ? null : bytes;
    }

    static async Task DispatchTransportAsync(MediaTransportRequest request)
    {
        await Transport.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!RemoteCommands.CanControl())
                return;
            switch (request)
            {
                case MediaTransportRequest.Play:
                    await RemoteCommands.PlayAsync().ConfigureAwait(false);
                    break;
                case MediaTransportRequest.Pause:
                    await RemoteCommands.PauseAsync().ConfigureAwait(false);
                    break;
                case MediaTransportRequest.Next:
                    await RemoteCommands.NextAsync().ConfigureAwait(false);
                    break;
                case MediaTransportRequest.Previous:
                    await RemoteCommands.PreviousAsync().ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            Transport.Release();
        }
    }

    static async Task DrainVolumeAsync()
    {
        var sent = int.MinValue;
        try
        {
            while (true)
            {
                var next = Interlocked.Exchange(ref _volumePending, int.MinValue);
                if (next == int.MinValue)
                    return;
                if (next == sent)
                    continue;
                sent = next;
                await RemoteCommands.SetActiveVolumeQuietAsync(next).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _volumeRunning, 0);
            if (Volatile.Read(ref _volumePending) != int.MinValue)
                RequestVolume(Volatile.Read(ref _volumePending));
        }
    }
}
