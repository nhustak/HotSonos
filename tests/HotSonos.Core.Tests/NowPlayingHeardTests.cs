using HotSonos.Core.Models;

namespace HotSonos.Core.Tests;

public class NowPlayingHeardTests
{
    [Theory]
    [InlineData("x-file-cifs://192.168.1.111/Music/Sonos/a.flac", "Sussudio", false)]
    [InlineData("x-file-smb://stormnas/Music/Jazz/a.mp3", "So What", false)]
    [InlineData("http://192.168.1.111/Music/Sonos/noext", "Blue in Green", false)]
    [InlineData("x-sonos-spotify:spotify%3atrack%3a6rqhFgbbKwnb9MLmUQDhG6?sid=12", "Kind of Blue", true)]
    [InlineData("x-rincon-mp3radio://opml.radiotime.com/Tune.ashx?id=1", "So What", true)]
    [InlineData("x-rincon-stream:RINCON_OFFICE", "Kind of Blue", true)]
    [InlineData("x-rincon-stream:RINCON_OFFICE", "TV", false)]
    [InlineData("x-rincon-queue:RINCON_OFFICE#0", "So What", false)]
    [InlineData("x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a37i9", "Discover Weekly", false)]
    [InlineData("x-sonos-spotify:spotify%3atrack%3aabc", "", false)]
    [InlineData("x-sonos-spotify:spotify%3atrack%3aabc", "Advertisement", false)]
    [InlineData("x-sonos-spotify:spotify%3atrack%3aabc", "x-sonos-spotify:spotify:track:abc", false)]
    public void IsHeardOutsideLibrary_keeps_named_non_library_tracks(string uri, string title, bool expected)
    {
        Assert.Equal(expected, NowPlaying.IsHeardOutsideLibrary(uri, title));
    }

    [Fact]
    public void UsesPerTrackUri_is_spotify_not_radio()
    {
        Assert.True(NowPlaying.UsesPerTrackUri(NowPlayingSourceKind.Spotify));
        Assert.False(NowPlaying.UsesPerTrackUri(NowPlayingSourceKind.TuneIn));
        Assert.False(NowPlaying.UsesPerTrackUri(NowPlayingSourceKind.LibrarySmb));
    }
}
