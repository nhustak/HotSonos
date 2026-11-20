namespace HotSonos.Remote;

/// <summary>
/// Hardware volume keys tell the open Player to move its number immediately.
/// The key still changes the house; <see cref="Refresh"/> re-reads after that write.
/// </summary>
static class PhoneVolumeKeys
{
    public static Action<int>? Note { get; set; }

    public static Action? Refresh { get; set; }
}
