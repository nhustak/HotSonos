using HotSonos.Core;
using HotSonos.Core.Models;
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
                var link = AppServices.Link;
                if (!link.Ready)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                    continue;
                }

                if (link.DrivePc && AppServices.Connection.IsPaired)
                {
                    var call = await AppServices.Client.GetStatusAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        return;
                    if (call.Kind == RemoteCallKind.Ok && call.Value is not null)
                    {
                        link.NotePcUp();
                        Status = call.Value;
                        Problem = null;
                    }
                    else
                    {
                        var text = call.Detail ?? RemoteFailureText.Describe(Map(call.Kind), null);
                        link.NotePcDown(text);
                        Status = null;
                        Problem = text;
                    }
                }
                else if (link.DrivePc)
                {
                    Status = null;
                    Problem = "Enter the PC address and PIN. Tap the line at the top.";
                }
                else if (link.Speakers.Zones.Count == 0)
                {
                    Status = null;
                    Problem = link.Problem ?? "Looking for speakers…";
                }
                else
                {
                    var snap = await link.Speakers.ReadSnapshotAsync(ct).ConfigureAwait(false);
                    Status = FromSpeakers(snap);
                    Problem = null;
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
                Problem = AppServices.Link.OnPc ? "Cannot reach the PC." : "Cannot reach the speakers.";
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    static RemoteFailureKind Map(RemoteCallKind kind) => kind switch
    {
        RemoteCallKind.Unauthorized => RemoteFailureKind.WrongPin,
        RemoteCallKind.Timeout => RemoteFailureKind.Timeout,
        RemoteCallKind.Cancelled => RemoteFailureKind.Cancelled,
        RemoteCallKind.Failed => RemoteFailureKind.Failed,
        _ => RemoteFailureKind.Unreachable,
    };

    static RemoteStatusResponse FromSpeakers(SonosHouseSnapshot snap)
    {
        var picture = LiveGrouping.Map(snap.Zones.Select(z =>
            new SpeakerMembership(z.RoomName, z.CoordinatorUuid, z.IsCoordinator)));
        var groups = picture.Groups
            .Select(g => new RemoteGroupDto(g.CoordinatorRoom, g.Label, g.Rooms.Count))
            .ToList();
        var active = picture.Groups.FirstOrDefault(g =>
            g.Rooms.Any(r => string.Equals(r, snap.ActiveRoom, StringComparison.OrdinalIgnoreCase)))?.Label
            ?? snap.ActiveRoom;
        var now = snap.Now;
        RemoteNowPlayingDto? playing = now is null
            ? null
            : new RemoteNowPlayingDto(
                now.Title,
                now.Artist,
                now.Album,
                now.State.ToString(),
                now.SourceLabel,
                now.CurrentTrack,
                now.NumberOfTracks,
                !string.IsNullOrWhiteSpace(now.AlbumArtUri),
                now.AlbumArtUri ?? "",
                now.Position is { } pos ? (int)pos.TotalSeconds : null,
                now.Duration is { } dur ? (int)dur.TotalSeconds : null);
        return new RemoteStatusResponse(
            true,
            "speakers",
            snap.ActiveRoom,
            active,
            groups,
            [],
            playing,
            snap.Volume,
            snap.Muted,
            false,
            false,
            picture.Groups.Count == 0 ? null : picture.Summary,
            null,
            null);
    }
}
