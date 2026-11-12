using HotSonos.Core;

namespace HotSonos.Core.Tests;

public class SpotifyItemTests
{
    [Theory]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "playlist", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc", "playlist", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/album/6wiUBliPe76YAVpNEdidpY", "album", "6wiUBliPe76YAVpNEdidpY")]
    [InlineData("spotify:track:6cpcorzV5cmVjBsuAXq4wD", "track", "6cpcorzV5cmVjBsuAXq4wD")]
    public void Parses_share_links(string text, string kind, string id)
    {
        Assert.True(SpotifyItem.TryParse(text, out var item));
        Assert.NotNull(item);
        Assert.Equal(kind, item!.Kind);
        Assert.Equal(id, item.Id);
        Assert.Equal($"spotify:{kind}:{id}", item.Canonical);
    }

    [Fact]
    public void Rejects_non_spotify_text()
    {
        Assert.False(SpotifyItem.TryParse("Morning Jazz", out _));
        Assert.False(SpotifyItem.TryParse("", out _));
    }

    [Fact]
    public void Playlist_playback_uses_container_uri_and_speaker_token_desc()
    {
        var item = new SpotifyItem("playlist", "37i9dQZF1DXcBWIGoYBM5M", "Discover Weekly");
        var playback = item.ToPlayback();

        Assert.True(playback.IsContainer);
        Assert.Equal(
            "x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a37i9dQZF1DXcBWIGoYBM5M",
            playback.Uri);
        Assert.Contains("SA_RINCON3079_X_#Svc3079-0-Token", playback.Metadata, StringComparison.Ordinal);
        Assert.Contains("Discover Weekly", playback.Metadata, StringComparison.Ordinal);
        Assert.Contains("object.container.playlistContainer", playback.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void Track_playback_uses_sonos_spotify_scheme()
    {
        Assert.True(SpotifyItem.TryParse("spotify:track:6cpcorzV5cmVjBsuAXq4wD", out var item));
        var playback = item!.ToPlayback(3079);

        Assert.False(playback.IsContainer);
        Assert.Equal("x-sonos-spotify:spotify:track:6cpcorzV5cmVjBsuAXq4wD", playback.Uri);
    }
}
