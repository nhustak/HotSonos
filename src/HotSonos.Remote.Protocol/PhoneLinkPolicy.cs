namespace HotSonos.Remote.Protocol;

/// <summary>How the phone decides between the tray app and the speakers.</summary>
public enum PhoneLinkPreference
{
    /// <summary>Try the paired PC first. Use the speakers when it does not answer.</summary>
    Auto = 0,

    /// <summary>Only the tray app. A missed PC stays a missed PC.</summary>
    Pc = 1,

    /// <summary>Only the speakers. The PC is not contacted.</summary>
    Standalone = 2,
}

/// <summary>What the phone is using after a reachability check.</summary>
public enum PhoneLinkOutcome
{
    Pc,
    Speakers,
    PcDown,
}

public static class PhoneLinkPolicy
{
    public static PhoneLinkOutcome Choose(PhoneLinkPreference preference, bool paired, bool pcReachable) =>
        preference switch
        {
            PhoneLinkPreference.Standalone => PhoneLinkOutcome.Speakers,
            PhoneLinkPreference.Pc => pcReachable ? PhoneLinkOutcome.Pc : PhoneLinkOutcome.PcDown,
            _ => paired && pcReachable ? PhoneLinkOutcome.Pc : PhoneLinkOutcome.Speakers,
        };

    public static string Banner(PhoneLinkOutcome outcome, string? pcHost, string? room) => outcome switch
    {
        PhoneLinkOutcome.Pc => string.IsNullOrWhiteSpace(pcHost) ? "PC" : "PC · " + pcHost.Trim(),
        PhoneLinkOutcome.PcDown => "PC unreachable",
        _ => string.IsNullOrWhiteSpace(room) ? "Speakers" : "Speakers · " + room.Trim(),
    };
}
