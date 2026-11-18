using HotSonos.Core;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>
/// Auto tries the paired PC, then the speakers. PC only never falls back.
/// Standalone never calls the PC. Resume runs <see cref="ResolveAsync"/> again.
/// </summary>
sealed class PhoneLink
{
    const string Key = "phone.link";

    public PhoneLinkPreference Preference { get; private set; } = PhoneLinkPreference.Auto;
    public PhoneLinkOutcome Outcome { get; private set; } = PhoneLinkOutcome.Speakers;
    public string Banner { get; private set; } = "Speakers";
    public string? Problem { get; private set; }

    /// <summary>False until the first reachability check finishes. The poll waits so it does not discover at the same time.</summary>
    public bool Ready { get; private set; }
    public SonosDirect Speakers { get; } = new();
    public event Action? Changed;

    public bool OnPc => Outcome == PhoneLinkOutcome.Pc;

    public PhoneLink()
    {
        var saved = Preferences.Get(Key, "auto");
        Preference = saved switch
        {
            "pc" => PhoneLinkPreference.Pc,
            "standalone" => PhoneLinkPreference.Standalone,
            _ => PhoneLinkPreference.Auto,
        };
        if (Preference != PhoneLinkPreference.Standalone && (Preference == PhoneLinkPreference.Pc || AppServices.Connection.IsPaired))
        {
            Outcome = PhoneLinkOutcome.Pc;
            Banner = PhoneLinkPolicy.Banner(PhoneLinkOutcome.Pc, AppServices.Connection.Host, null);
        }
    }

    public void SetPreference(PhoneLinkPreference preference)
    {
        Preference = preference;
        Preferences.Set(Key, preference switch
        {
            PhoneLinkPreference.Pc => "pc",
            PhoneLinkPreference.Standalone => "standalone",
            _ => "auto",
        });
    }

    public void SelectRoom(string room)
    {
        Speakers.SelectRoom(room);
        if (!OnPc)
            Publish(PhoneLinkOutcome.Speakers, null);
    }

    public async Task ResolveAsync(CancellationToken ct)
    {
        var paired = AppServices.Connection.IsPaired;
        var reachable = false;
        if (Preference != PhoneLinkPreference.Standalone && paired)
        {
            var connection = AppServices.Connection;
            AppServices.Client.Apply(connection.Host, connection.Port, connection.Pin);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var call = await AppServices.Client.GetStatusAsync(limit.Token).ConfigureAwait(false);
                reachable = call.Kind == RemoteCallKind.Ok;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                reachable = false;
            }
        }

        var outcome = PhoneLinkPolicy.Choose(Preference, paired, reachable);
        if (outcome == PhoneLinkOutcome.Speakers)
        {
            try
            {
                using (MulticastHold.Acquire())
                    await Speakers.DiscoverAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                Publish(outcome, Speakers.Zones.Count == 0 ? "No speakers answered." : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Publish(outcome, ex.Message);
            }

            return;
        }

        Publish(outcome, outcome == PhoneLinkOutcome.PcDown ? "Cannot reach the PC." : null);
    }

    void Publish(PhoneLinkOutcome outcome, string? problem)
    {
        Outcome = outcome;
        Problem = problem;
        Ready = true;
        var host = AppServices.Connection.Host;
        Banner = PhoneLinkPolicy.Banner(outcome, host, Speakers.ActiveZone?.RoomName);
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
    }
}
