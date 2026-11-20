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

    /// <summary>
    /// Chip text. A live PC is just "PC" — the address stays on Connect and Settings → Connection / PC.
    /// </summary>
    public static string Banner(PhoneLinkOutcome outcome, string? pcHost, string? room) => outcome switch
    {
        PhoneLinkOutcome.Pc => "PC",
        PhoneLinkOutcome.PcDown => string.IsNullOrWhiteSpace(pcHost) ? "PC: not paired" : "PC unreachable",
        _ => string.IsNullOrWhiteSpace(room) ? "Speakers" : "Speakers: " + room.Trim(),
    };
}

/// <summary>Whether the phone should open Connect before the tabs.</summary>
public static class PhoneLaunch
{
    /// <summary>
    /// First launch, and any later launch with no saved pairing, opens Connect.
    /// Speakers only is an explicit choice and opens the tabs without a PC.
    /// A saved address and PIN opens the tabs.
    /// </summary>
    public static bool OpenConnectScreen(PhoneLinkPreference preference, bool paired) =>
        !paired && preference != PhoneLinkPreference.Standalone;
}
