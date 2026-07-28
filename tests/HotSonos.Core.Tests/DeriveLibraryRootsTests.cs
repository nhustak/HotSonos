using HotSonos.Core;

namespace HotSonos.Core.Tests;

public class DeriveLibraryRootsTests
{
    [Fact]
    public void DeriveLibraryRoots_splits_share_into_top_level_folders()
    {
        // Same shape as a multi-folder Sonos Music Library on one SMB share.
        string[] files =
        [
            @"\\192.168.1.111\Music\Jazz\Artist A\Track.flac",
            @"\\192.168.1.111\Music\Jazz\Artist B\Track.flac",
            @"\\192.168.1.111\Music\Sonos\Phil Collins\Sussudio.flac",
            @"\\192.168.1.111\Music\Sonos\Heart\Alone.flac",
            @"\\192.168.1.111\Music\New Age\Artist A\Ambient.flac",
            @"\\192.168.1.111\Music\New Age\Artist B\Ambient.flac",
            @"\\192.168.1.111\Music\Music Instrumental\Artist A\Theme.flac",
            @"\\192.168.1.111\Music\Music Instrumental\Artist B\Theme.flac",
            @"\\192.168.1.111\Music\Seasonal\Christmas\Artist\Carol.flac",
        ];

        var roots = SonosController.DeriveLibraryRoots(files);

        Assert.Equal(5, roots.Count);
        Assert.Contains(@"\\192.168.1.111\Music\Jazz", roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"\\192.168.1.111\Music\Sonos", roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"\\192.168.1.111\Music\New Age", roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"\\192.168.1.111\Music\Music Instrumental", roots, StringComparer.OrdinalIgnoreCase);
        // Nested Sonos Music Library folder (only content under Christmas) → Christmas, not Seasonal.
        Assert.Contains(@"\\192.168.1.111\Music\Seasonal\Christmas", roots, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\192.168.1.111\Music\Seasonal", roots, StringComparer.OrdinalIgnoreCase);
        // Must NOT collapse to the share alone.
        Assert.DoesNotContain(@"\\192.168.1.111\Music", roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeriveLibraryRoots_christmas_nested_folder_named_christmas()
    {
        string[] files =
        [
            @"\\192.168.1.111\Music\Seasonal\Christmas\Artist\Carol.flac",
            @"\\192.168.1.111\Music\Seasonal\Christmas\Other\Song.flac",
        ];
        var roots = SonosController.DeriveLibraryRoots(files);
        Assert.Single(roots);
        Assert.Equal(@"\\192.168.1.111\Music\Seasonal\Christmas", roots[0], ignoreCase: true);
    }

    [Fact]
    public void DeriveLibraryRoots_single_folder_library_uses_that_folder()
    {
        string[] files =
        [
            @"\\nas\share\Sonos\A\1.flac",
            @"\\nas\share\Sonos\B\2.flac",
        ];

        var roots = SonosController.DeriveLibraryRoots(files);
        Assert.Single(roots);
        Assert.Equal(@"\\nas\share\Sonos", roots[0], ignoreCase: true);
    }

    [Fact]
    public void DeriveLibraryRoots_files_directly_on_share_use_share()
    {
        string[] files =
        [
            @"\\nas\share\song1.flac",
            @"\\nas\share\song2.flac",
        ];

        var roots = SonosController.DeriveLibraryRoots(files);
        Assert.Single(roots);
        Assert.Equal(@"\\nas\share", roots[0], ignoreCase: true);
    }

    [Fact]
    public void DeriveLibraryRoots_empty_is_empty()
    {
        Assert.Empty(SonosController.DeriveLibraryRoots([]));
    }
}
