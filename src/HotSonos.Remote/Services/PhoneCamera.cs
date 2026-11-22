namespace HotSonos.Remote;

/// <summary>
/// Camera is optional. The manifest keeps <c>android.hardware.camera</c> required=false
/// so a Chromebook with no camera can still install. Scan QR is hidden in that case.
/// </summary>
static class PhoneCamera
{
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return ZXing.Net.Maui.BarcodeScanning.IsSupported;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task<bool> EnsurePermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>().ConfigureAwait(true);
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>().ConfigureAwait(true);
        return status == PermissionStatus.Granted;
    }
}
