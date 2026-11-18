using Android.Content;
using Android.Net.Wifi;

namespace HotSonos.Remote;

/// <summary>
/// SSDP multicast is dropped on Android unless the process holds a WifiManager multicast lock.
/// </summary>
static class MulticastHold
{
    public static IDisposable Acquire()
    {
        var wifi = Android.App.Application.Context.GetSystemService(Context.WifiService) as WifiManager;
        var loc = wifi?.CreateMulticastLock("hotsonos");
        if (loc is null)
            return Empty.Instance;
        loc.SetReferenceCounted(false);
        loc.Acquire();
        return new Releaser(loc);
    }

    sealed class Releaser(WifiManager.MulticastLock loc) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (loc.IsHeld)
                    loc.Release();
            }
            catch (Java.Lang.Exception)
            {
                // The lock was already released with the wifi radio.
            }
        }
    }

    sealed class Empty : IDisposable
    {
        public static readonly Empty Instance = new();
        public void Dispose() { }
    }
}
