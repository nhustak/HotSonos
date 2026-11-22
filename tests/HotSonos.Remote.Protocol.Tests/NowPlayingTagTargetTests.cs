using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class NowPlayingTagTargetTests
{
    [Fact]
    public void Resolve_requires_a_library_path_for_the_playing_track()
    {
        var empty = NowPlayingTagTarget.Resolve(empty: true, missingUri: true, libraryPath: null);
        Assert.False(empty.CanTag);
        Assert.Equal(NowPlayingTagTarget.NothingPlaying, empty.Message);

        var noUri = NowPlayingTagTarget.Resolve(empty: false, missingUri: true, libraryPath: null);
        Assert.False(noUri.CanTag);
        Assert.Equal(NowPlayingTagTarget.NoUri, noUri.Message);

        var stream = NowPlayingTagTarget.Resolve(empty: false, missingUri: false, libraryPath: "  ");
        Assert.False(stream.CanTag);
        Assert.Equal(NowPlayingTagTarget.NotInLibrary, stream.Message);

        var library = NowPlayingTagTarget.Resolve(empty: false, missingUri: false, libraryPath: @" \\nas\music\a.flac ");
        Assert.True(library.CanTag);
        Assert.Equal(@"\\nas\music\a.flac", library.Path);
        Assert.Null(library.Message);
    }
}
