namespace HotSonos.Remote;

/// <summary>
/// Android holds a WifiManager multicast lock in the Android copy of this type.
/// iOS has no equivalent. UDP 42343 and SSDP need
/// <c>com.apple.developer.networking.multicast</c>, which is not in the signed
/// app until Apple approves it. Callers treat a miss as "type the PC address".
/// </summary>
static class MulticastHold
{
    public static IDisposable Acquire() => Empty.Instance;

    sealed class Empty : IDisposable
    {
        public static readonly Empty Instance = new();
        public void Dispose() { }
    }
}
