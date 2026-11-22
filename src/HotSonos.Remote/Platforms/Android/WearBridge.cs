using Android.Content;
using Android.Gms.Common;
using Android.Gms.Wearable;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>
/// Forwards a Wear OS companion's Data Layer commands to the PC client or the standalone engine,
/// and pushes the polled now-playing snapshot back. This is independent of the media-session
/// switch: that switch is the system media controls, and this is the companion app.
/// </summary>
static class WearBridge
{
    static readonly object Gate = new();
    static readonly SemaphoreSlim ResolveGate = new(1, 1);
    static readonly HashSet<string> Nodes = new(StringComparer.Ordinal);

    static bool _installed;
    static int? _target;
    static long _targetAt;

    public static void Install()
    {
        lock (Gate)
        {
            if (_installed)
                return;
            _installed = true;
        }

        AppServices.Live.Changed += OnPolled;
    }

    public static void OnCommand(string? nodeId, string? payload)
    {
        Remember(nodeId);
        var command = WearWire.ParseCommand(payload);
        if (command is null)
            return;
        _ = HandleAsync(command);
    }

    static async Task HandleAsync(string command)
    {
        try
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            switch (command)
            {
                case WearWire.Play:
                    if (RemoteCommands.CanControl())
                        await RemoteCommands.PlayAsync().ConfigureAwait(false);
                    break;
                case WearWire.Pause:
                    if (RemoteCommands.CanControl())
                        await RemoteCommands.PauseAsync().ConfigureAwait(false);
                    break;
                case WearWire.Next:
                    if (RemoteCommands.CanControl())
                        await RemoteCommands.NextAsync().ConfigureAwait(false);
                    break;
                case WearWire.Previous:
                    if (RemoteCommands.CanControl())
                        await RemoteCommands.PreviousAsync().ConfigureAwait(false);
                    break;
                case WearWire.VolumeUp:
                    StepAndSend(1);
                    break;
                case WearWire.VolumeDown:
                    StepAndSend(-1);
                    break;
            }

            Push();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("HotSonos", "Watch command was not handled: " + ex.Message);
        }
    }

    static void StepAndSend(int direction)
    {
        var state = MediaControlMapping.FromStatus(AppServices.Live.Status);
        int next;
        lock (Gate)
        {
            var held = WearWire.ReconcileTarget(
                _target,
                state.HasVolume ? state.VolumePercent : null,
                Environment.TickCount64 - _targetAt);
            var current = held
                ?? (state.HasVolume ? state.VolumePercent : WearWire.UnknownVolume);
            next = WearWire.StepVolume(current, direction, PhoneVolume.SpeakerStep);
            _target = next;
            _targetAt = Environment.TickCount64;
        }

        MediaControls.RequestVolume(next);
    }

    static void OnPolled()
    {
        var state = MediaControlMapping.FromStatus(AppServices.Live.Status);
        lock (Gate)
        {
            _target = WearWire.ReconcileTarget(
                _target,
                state.HasVolume ? state.VolumePercent : null,
                Environment.TickCount64 - _targetAt);
        }

        Push();
    }

    static void Remember(string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return;
        lock (Gate)
            Nodes.Add(nodeId);
    }

    static void Push()
    {
        string[] nodes;
        int? target;
        lock (Gate)
        {
            if (Nodes.Count == 0)
                return;
            nodes = Nodes.ToArray();
            target = _target;
        }

        var context = HostContext();
        if (context is null || !PlayServicesReady(context))
            return;

        var json = WearWire.EncodeState(
            MediaControlMapping.FromStatus(AppServices.Live.Status),
            target,
            AppServices.Live.Upcoming);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        try
        {
            var client = WearableClass.GetMessageClient(context);
            foreach (var node in nodes)
            {
                _ = client.SendMessageAsync(node, WearWire.Path, bytes).ContinueWith(
                    t => Android.Util.Log.Warn("HotSonos", "Watch state was not sent: " + t.Exception?.GetBaseException().Message),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("HotSonos", "Watch state was not sent: " + ex.Message);
        }
    }

    static async Task EnsureReadyAsync()
    {
        Install();
        await ResolveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var connection = AppServices.Connection;
            if (connection.IsPaired)
                AppServices.Client.Apply(connection.Host, connection.Port, connection.Pin);
            if (!AppServices.Live.IsRunning)
                AppServices.Live.Start();
            if (!AppServices.Link.Ready)
                await AppServices.Link.ResolveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ResolveGate.Release();
        }
    }

    static Context? HostContext()
    {
        if (Platform.AppContext is Context maui)
            return maui;
        try
        {
            return Android.App.Application.Context;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// ChromeOS and a phone without Play services must keep running. A missing library
    /// or a failed availability check means there is no watch to update.
    /// </summary>
    static bool PlayServicesReady(Context context)
    {
        try
        {
            return GoogleApiAvailability.Instance.IsGooglePlayServicesAvailable(context) == ConnectionResult.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
