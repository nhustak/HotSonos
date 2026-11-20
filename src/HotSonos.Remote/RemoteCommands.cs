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
