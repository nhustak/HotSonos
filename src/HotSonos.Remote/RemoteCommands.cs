using HotSonos.Core;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>
/// Transport for keyboard and media keys. The same split as the Player buttons:
/// PC when <see cref="PhoneLink.DrivePc"/> is set, otherwise the speakers.
/// Volume keys stay on the hardware-key path so the step matches.
/// </summary>
static class RemoteCommands
{
    public static bool CanControl() =>
        AppServices.Link.OnPc
            ? AppServices.Connection.IsPaired
            : AppServices.Link.Speakers.Zones.Count > 0;

    public static Task PlayPauseAsync() => Run(
        (speaker, ct) => speaker.PlayPauseAsync(ct),
        ct => AppServices.Client.PlayPauseAsync(ct));

    /// <summary>
    /// Session play. Speakers get an explicit Play. PC mode only has a toggle, so this
    /// sends it when the last poll is not already playing.
    /// </summary>
    public static Task PlayAsync() => SendPlayPause(MediaTransportRequest.Play);

    /// <summary>Session pause. Same split as <see cref="PlayAsync"/>.</summary>
    public static Task PauseAsync() => SendPlayPause(MediaTransportRequest.Pause);

    /// <summary>
    /// Absolute 0–100 for the active room's group. PC mode uses the tray's house volume.
    /// Failures stay quiet; the next poll puts the slider back.
    /// </summary>
    public static async Task SetActiveVolumeQuietAsync(int percent)
    {
        percent = MediaControlMapping.ClampVolume(percent);
        try
        {
            if (AppServices.Link.DrivePc)
            {
                if (!AppServices.Connection.IsPaired)
                    return;
                await AppServices.Client.SetVolumeAsync(percent, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var speakers = AppServices.Link.Speakers;
                var zone = speakers.ActiveZone;
                if (zone is null)
                    return;
                var ips = speakers.Zones
                    .Where(z => string.Equals(z.CoordinatorUuid, zone.CoordinatorUuid, StringComparison.OrdinalIgnoreCase))
                    .Select(z => z.IpAddress)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (ips.Count == 0)
                    ips.Add(zone.IpAddress);
                await Task.WhenAll(ips.Select(ip => SetSpeakerVolumeSoft(speakers, ip, percent))).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The next status poll is the correction.
        }

        MainThread.BeginInvokeOnMainThread(() => PhoneVolumeKeys.Refresh?.Invoke());
    }

    static async Task SetSpeakerVolumeSoft(SonosDirect speakers, string ip, int percent)
    {
        try
        {
            await speakers.SetVolumeAsync(ip, percent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A bonded unit that rejects SetVolume does not block the rest of the group.
        }
    }

    static Task SendPlayPause(MediaTransportRequest request)
    {
        var actual = MediaControlMapping.FromStatus(AppServices.Live.Status).Playback;
        if (!MediaControlMapping.ShouldSendPlayPause(actual, request))
            return Task.CompletedTask;
        if (AppServices.Link.DrivePc)
            return PlayPauseAsync();
        var play = request == MediaTransportRequest.Play;
        return Run(
            (speaker, ct) => play ? speaker.PlayAsync(ct) : speaker.PauseAsync(ct),
            ct => AppServices.Client.PlayPauseAsync(ct));
    }

    public static Task NextAsync() => Run(
        (speaker, ct) => speaker.NextAsync(ct),
        ct => AppServices.Client.NextAsync(ct));

    public static Task PreviousAsync() => Run(
        (speaker, ct) => speaker.PreviousAsync(ct),
        ct => AppServices.Client.PreviousAsync(ct));

    public static async Task MuteAsync()
    {
        await Run(
            async (_, ct) => { await AppServices.Link.Speakers.ToggleGroupMuteAsync(ct).ConfigureAwait(false); },
            ct => AppServices.Client.MuteAsync(ct)).ConfigureAwait(false);
        MainThread.BeginInvokeOnMainThread(() => PhoneVolumeKeys.Refresh?.Invoke());
    }

    static async Task Run(
        Func<SonosController, CancellationToken, Task> speakers,
        Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>> pc)
    {
        try
        {
            if (AppServices.Link.DrivePc)
            {
                var result = await pc(CancellationToken.None).ConfigureAwait(false);
                if (result.Kind == RemoteCallKind.Unauthorized)
                {
                    await Alert(result.Detail ?? "Wrong PIN. The PC rejected it. Open connection and enter the PIN from the PC Phone window.").ConfigureAwait(false);
                    return;
                }

                if (result.Kind != RemoteCallKind.Ok || result.Value is not { Ok: true })
                    await Alert(result.Value?.Error ?? result.Detail ?? "Could not reach the PC.").ConfigureAwait(false);
                return;
            }

            var speaker = AppServices.Link.Speakers.Controller
                ?? throw new InvalidOperationException("No speakers yet. Pull to refresh, or check that the phone is on the same Wi-Fi.");
            await speakers(speaker, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Alert(string.IsNullOrWhiteSpace(ex.Message) ? "Could not reach the speakers." : ex.Message).ConfigureAwait(false);
        }
    }

    static Task Alert(string message) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (Application.Current?.MainPage is Page page)
                await page.DisplayAlert("HotSonos", message, "OK");
        });
}
