using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>
/// Polls <c>GET /api/status</c> every 2 seconds while the shell is open.
/// Playback changes without a phone action, and the status route is an in-memory read on the PC.
/// A push socket would be a second long-lived connection for the same JSON. Heavier lists refresh
/// when their tab appears.
/// </summary>
sealed class RemoteLive
{
    readonly object _gate = new();
    CancellationTokenSource? _loop;

    public RemoteStatusResponse? Status { get; private set; }
    public string? Problem { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        lock (_gate)
            _loop = cts;
        _ = Loop(cts.Token);
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _loop;
            _loop = null;
        }

        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var call = await AppServices.Client.GetStatusAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;
                if (call.Kind == RemoteCallKind.Ok && call.Value is not null)
                {
                    Status = call.Value;
                    Problem = null;
                }
                else if (call.Kind == RemoteCallKind.Unauthorized)
                {
                    Problem = "The PC rejected the PIN.";
                }
                else if (call.Kind == RemoteCallKind.Unreachable)
                {
                    Problem = "Cannot reach the PC.";
                }

                MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                Problem = "Cannot reach the PC.";
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
