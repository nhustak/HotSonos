namespace HotSonos.Remote;

/// <summary>
/// Copy shown when UDP discovery cannot run. The multicast entitlement is not
/// in the signed app until Apple approves it, so Find PC and SSDP miss and the
/// address fields stay editable.
/// </summary>
static class IosDiscovery
{
    public const string FindPcMiss =
        "No PC answered. Type the address from the Phone window. iOS blocks this UDP search until Apple approves the multicast entitlement.";

    public const string SpeakersMiss =
        "No speakers answered. Type the PC address on Connect. iOS blocks speaker search until Apple approves the multicast entitlement.";
}
