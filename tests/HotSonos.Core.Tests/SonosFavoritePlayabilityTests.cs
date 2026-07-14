using HotSonos.Core.Models;

namespace HotSonos.Core.Tests;

public class SonosFavoritePlayabilityTests
{
    [Fact]
    public void Favorite_requires_uri()
    {
        var withUri = new SonosFavorite
        {
            Id = "FV:2/1",
            Kind = SonosFavoriteKind.Favorite,
            Title = "Jazz",
            Uri = "x-sonosapi-stream:s123",
            Metadata = "<DIDL-Lite/>",
        };
        var shortcut = withUri with { Uri = "" };

        Assert.True(withUri.IsPlayable);
        Assert.False(shortcut.IsPlayable);
    }

    [Fact]
    public void Playlist_is_playable_by_id_even_without_res()
    {
        var playlist = new SonosFavorite
        {
            Id = "SQ:0",
            Kind = SonosFavoriteKind.Playlist,
            Title = "Morning",
            Uri = "", // Sonos often omits a usable <res> for SQ: containers
            Metadata = "",
        };

        Assert.True(playlist.IsPlayable);
    }

    [Fact]
    public void Playlist_with_file_uri_still_playable()
    {
        var playlist = new SonosFavorite
        {
            Id = "SQ:3",
            Kind = SonosFavoriteKind.Playlist,
            Title = "Gym",
            Uri = "file:///jffs/settings/savedqueues.rsq#3",
            Metadata = "",
        };

        Assert.True(playlist.IsPlayable);
    }

    [Fact]
    public void Playlist_without_id_is_not_playable()
    {
        var playlist = new SonosFavorite
        {
            Id = "",
            Kind = SonosFavoriteKind.Playlist,
            Title = "Broken",
            Uri = "",
            Metadata = "",
        };

        Assert.False(playlist.IsPlayable);
    }
}
