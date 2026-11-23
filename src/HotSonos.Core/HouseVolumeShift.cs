namespace HotSonos.Core;

/// <summary>
/// Moves every room by the same delta as a reference room so per-room offsets
/// stay intact. The phone does not carry the tray's offset table, so a house
/// set has to follow the reference instead of writing the same raw percent
/// onto every speaker.
/// </summary>
public static class HouseVolumeShift
{
    /// <summary>
    /// Raw volume for one member when the reference room goes from
    /// <paramref name="referenceFrom"/> to <paramref name="referenceTo"/>.
    /// 0 is a legal target.
    /// </summary>
    public static int Follow(int memberVolume, int referenceFrom, int referenceTo)
    {
        memberVolume = Math.Clamp(memberVolume, 0, 100);
        referenceFrom = Math.Clamp(referenceFrom, 0, 100);
        referenceTo = Math.Clamp(referenceTo, 0, 100);
        return Math.Clamp(memberVolume + (referenceTo - referenceFrom), 0, 100);
    }
}
