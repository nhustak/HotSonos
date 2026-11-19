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
    public PhoneLinkOutcome Outcome { get; private set; } = PhoneLinkOutcome.PcDown;
    public string Banner { get; private set; } = "PC: not paired";
    public string? Problem { get; private set; }

    /// <summary>False until the first reachability check finishes. The poll waits so it does not discover at the same time.</summary>
    public bool Ready { get; private set; }
    public SonosDirect Speakers { get; } = new();
    public event Action? Changed;

    public bool OnPc => Outcome == PhoneLinkOutcome.Pc;

    /// <summary>PC only, or a live PC. Screens use the PC API and do not substitute speakers.</summary>
    public bool DrivePc => Preference == PhoneLinkPreference.Pc || OnPc;

    public PhoneLink()
    {
        var saved = Preferences.Get(Key, "auto");
        Preference = saved switch
        {
            "pc" => PhoneLinkPreference.Pc,
            "standalone" => PhoneLinkPreference.Standalone,
            _ => PhoneLinkPreference.Auto,
        };
        if (Preference == PhoneLinkPreference.Standalone)
        {
            Outcome = PhoneLinkOutcome.Speakers;
            Banner = "Speakers";
        }
        else if (AppServices.Connection.IsPaired)
        {
            Outcome = PhoneLinkOutcome.Pc;
            Banner = PhoneLinkPolicy.Banner(PhoneLinkOutcome.Pc, AppServices.Connection.Host, null);
        }
        else
        {
            Outcome = PhoneLinkOutcome.PcDown;
            Banner = "PC: not paired";
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

    /// <summary>A status poll reached the PC. PC-only mode recovers without waiting for resume.</summary>
    public void NotePcUp()
    {
        if (Preference == PhoneLinkPreference.Standalone)
            return;
        if (OnPc && Problem is null)
            return;
        Publish(PhoneLinkOutcome.Pc, null);
    }

    /// <summary>A status poll missed the PC. Auto does not leave a live PC session on one miss.</summary>
    public void NotePcDown(string problem)
    {
        if (Preference == PhoneLinkPreference.Pc)
        {
            Publish(PhoneLinkOutcome.PcDown, problem);
            return;
        }

        if (Outcome == PhoneLinkOutcome.Pc)
        {
            Problem = problem;
            Ready = true;
            MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
        }
    }

    public async Task ResolveAsync(CancellationToken ct)
    {
        var paired = AppServices.Connection.IsPaired;
        var reachable = false;
        string? miss = null;
        if (Preference != PhoneLinkPreference.Standalone && paired)
        {
            var connection = AppServices.Connection;
            AppServices.Client.Apply(connection.Host, connection.Port, connection.Pin);
            var wait = Preference == PhoneLinkPreference.Pc ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(2);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(wait);
            try
            {
                var call = await AppServices.Client.GetStatusAsync(limit.Token).ConfigureAwait(false);
                reachable = call.Kind == RemoteCallKind.Ok;
                if (!reachable)
                    miss = call.Detail;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                reachable = false;
                miss = RemoteFailureText.Describe(RemoteFailureKind.Timeout, null);
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

        var problem = outcome == PhoneLinkOutcome.PcDown
            ? miss ?? (paired ? "Cannot reach the PC." : "Enter the PC address and PIN.")
            : null;
        Publish(outcome, problem);
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
