namespace HotSonos.Core.Tests;

public class HouseVolumeShiftTests
{
    [Fact]
    public void Follow_keeps_room_offsets_when_the_reference_goes_to_zero()
    {
        // Office (offset 0) 15% → 0; Kitchen +5 and Theater +60 move by the same delta.
        Assert.Equal(0, HouseVolumeShift.Follow(15, 15, 0));
        Assert.Equal(5, HouseVolumeShift.Follow(20, 15, 0));
        Assert.Equal(60, HouseVolumeShift.Follow(75, 15, 0));
        Assert.Equal(15, HouseVolumeShift.Follow(15, 15, 15));
    }

    [Fact]
    public void Follow_clamps_and_allows_a_zero_start()
    {
        Assert.Equal(0, HouseVolumeShift.Follow(3, 3, 0));
        Assert.Equal(0, HouseVolumeShift.Follow(0, 2, 0));
        Assert.Equal(100, HouseVolumeShift.Follow(90, 10, 25));
        Assert.Equal(0, HouseVolumeShift.Follow(1, 15, 0));
    }

    [Fact]
    public void Phone_house_volume_follows_the_reference_instead_of_a_flat_raw_set()
    {
        var direct = Source("src", "HotSonos.Core", "SonosDirect.cs");
        Assert.Contains("HouseVolumeShift.Follow", direct, StringComparison.Ordinal);
        Assert.Contains("GroupMemberIps", direct, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await Task.WhenAll(ips.Select(ip => SetVolumeSoft(ip, percent, ct)))",
            direct,
            StringComparison.Ordinal);

        var commands = Source("src", "HotSonos.Remote", "RemoteCommands.cs");
        Assert.Contains("SetHouseVolumeAsync(percent", commands, StringComparison.Ordinal);
    }

    static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new InvalidOperationException("Could not find " + Path.Combine(parts));
    }
}
