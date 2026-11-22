using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class MediaControlMappingTests
{
    [Theory]
    [InlineData("Playing", MediaControlPlayback.Playing)]
    [InlineData("PLAYING", MediaControlPlayback.Playing)]
    [InlineData("Transitioning", MediaControlPlayback.Playing)]
    [InlineData("TRANSITIONING", MediaControlPlayback.Playing)]
    [InlineData("Paused", MediaControlPlayback.Paused)]
    [InlineData("PausedPlayback", MediaControlPlayback.Paused)]
    [InlineData("PAUSED_PLAYBACK", MediaControlPlayback.Paused)]
    [InlineData("Stopped", MediaControlPlayback.Stopped)]
    [InlineData("STOPPED", MediaControlPlayback.Stopped)]
    [InlineData("Unknown", MediaControlPlayback.Stopped)]
    [InlineData("", MediaControlPlayback.Stopped)]
    [InlineData(null, MediaControlPlayback.Stopped)]
    public void Playback_state_maps_enum_names_and_raw_upnp(string? raw, MediaControlPlayback expected) =>
        Assert.Equal(expected, MediaControlMapping.MapPlayback(raw));

    [Fact]
    public void Status_fills_title_artist_album_and_clamps_volume()
    {
        var state = MediaControlMapping.FromStatus(Status(
            "Afternoon",
            "Ada",
            "Hours",
            "Playing",
            volume: 140,
            muted: false,
            hasArt: true,
            art: "abc123",
            position: 12,
            duration: 180));

        Assert.Equal("Afternoon", state.Title);
        Assert.Equal("Ada", state.Artist);
        Assert.Equal("Hours", state.Album);
        Assert.Equal(MediaControlPlayback.Playing, state.Playback);
        Assert.Equal(MediaControlPlayback.Playing, state.PublishedPlayback);
        Assert.Equal(100, state.VolumePercent);
        Assert.True(state.HasVolume);
        Assert.False(state.Muted);
        Assert.True(state.ShowArt);
        Assert.Equal("abc123", state.ArtToken);
        Assert.Equal(12_000, state.PositionMs);
        Assert.Equal(180_000, state.DurationMs);
        Assert.Equal(1f, state.PlaybackSpeed);
        Assert.Equal("Ada — Hours", MediaControlMapping.NotificationText(state));
    }

    [Fact]
    public void Empty_status_is_a_paused_session_with_no_volume()
    {
        var state = MediaControlMapping.FromStatus(null);

        Assert.Equal("Nothing playing", state.Title);
        Assert.Equal("", state.Artist);
        Assert.Equal("", state.Album);
        Assert.Equal(MediaControlPlayback.Stopped, state.Playback);
        Assert.Equal(MediaControlPlayback.Paused, state.PublishedPlayback);
        Assert.Equal(0, state.VolumePercent);
        Assert.False(state.HasVolume);
        Assert.False(state.ShowArt);
        Assert.Null(state.ArtToken);
        Assert.Equal(0, state.PositionMs);
        Assert.Equal(0, state.DurationMs);
        Assert.Equal(0f, state.PlaybackSpeed);
        Assert.Equal("HotSonos", MediaControlMapping.NotificationText(state));
    }

    [Fact]
    public void Blank_title_and_stopped_transport_publish_as_paused()
    {
        var state = MediaControlMapping.FromStatus(Status("  ", "  ", null, "Stopped", volume: -4, muted: true));

        Assert.Equal("Nothing playing", state.Title);
        Assert.Equal("", state.Artist);
        Assert.Equal("", state.Album);
        Assert.Equal(MediaControlPlayback.Stopped, state.Playback);
        Assert.Equal(MediaControlPlayback.Paused, state.PublishedPlayback);
        Assert.Equal(0, state.VolumePercent);
        Assert.True(state.HasVolume);
        Assert.True(state.Muted);
        Assert.Equal("HotSonos · muted", MediaControlMapping.NotificationText(state));
    }

    [Fact]
    public void Paused_playback_publishes_paused_and_keeps_the_percent()
    {
        var state = MediaControlMapping.FromStatus(Status("Song", "Artist", "", "PausedPlayback", volume: 33, muted: true));

        Assert.Equal(MediaControlPlayback.Paused, state.Playback);
        Assert.Equal(MediaControlPlayback.Paused, state.PublishedPlayback);
        Assert.Equal(33, state.VolumePercent);
        Assert.Equal(0f, state.PlaybackSpeed);
        Assert.Equal("Artist · muted", MediaControlMapping.NotificationText(state));
    }

    [Theory]
    [InlineData(MediaControlPlayback.Playing, MediaTransportRequest.Play, false)]
    [InlineData(MediaControlPlayback.Playing, MediaTransportRequest.Pause, true)]
    [InlineData(MediaControlPlayback.Paused, MediaTransportRequest.Play, true)]
    [InlineData(MediaControlPlayback.Paused, MediaTransportRequest.Pause, false)]
    [InlineData(MediaControlPlayback.Stopped, MediaTransportRequest.Play, true)]
    [InlineData(MediaControlPlayback.Stopped, MediaTransportRequest.Pause, false)]
    [InlineData(MediaControlPlayback.Playing, MediaTransportRequest.Toggle, true)]
    [InlineData(MediaControlPlayback.Playing, MediaTransportRequest.Next, false)]
    public void Play_and_pause_are_sent_only_when_they_change_the_transport(
        MediaControlPlayback actual,
        MediaTransportRequest request,
        bool send) =>
        Assert.Equal(send, MediaControlMapping.ShouldSendPlayPause(actual, request));

    [Theory]
    [InlineData(40, 1, 41)]
    [InlineData(40, -1, 39)]
    [InlineData(0, -1, 0)]
    [InlineData(100, 1, 100)]
    [InlineData(10, 0, null)]
    public void Relative_volume_steps_one_percent(int current, int direction, int? expected) =>
        Assert.Equal(expected, MediaControlMapping.AdjustVolume(current, direction));

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Media_controls_default_on(bool? stored, bool enabled) =>
        Assert.Equal(enabled, MediaControlMapping.PreferenceEnabled(stored));

    [Theory]
    [InlineData("http://192.168.1.20:1400/getaa?s=1&u=abc", true)]
    [InlineData("https://192.168.1.20/art.jpg", true)]
    [InlineData("abc123def456", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("http://user:pass@192.168.1.20/art", false)]
    [InlineData("file:///tmp/art.jpg", false)]
    public void Direct_art_is_an_http_uri_without_userinfo(string? token, bool direct) =>
        Assert.Equal(direct, MediaControlMapping.IsDirectArtUri(token));

    [Fact]
    public void Face_ignores_position_and_volume_and_tracks_the_track()
    {
        var playing = MediaControlMapping.FromStatus(Status("Song", "Ada", "Hours", "Playing", 20, false, true, "art", 1, 10));
        var later = MediaControlMapping.FromStatus(Status("Song", "Ada", "Hours", "Playing", 21, false, true, "art", 4, 10));
        var paused = MediaControlMapping.FromStatus(Status("Song", "Ada", "Hours", "PausedPlayback", 20, false, true, "art", 4, 10));

        Assert.True(playing.SameFace(later));
        Assert.False(playing.SameFace(paused));
    }

    static RemoteStatusResponse Status(
        string? title,
        string? artist,
        string? album,
        string state,
        int? volume,
        bool? muted,
        bool hasArt = false,
        string art = "",
        int? position = null,
        int? duration = null)
    {
        var now = new RemoteNowPlayingDto(title, artist, album, state, "Library", 1, 2, hasArt, art, position, duration);
        return new RemoteStatusResponse(true, "test", "Office", "Office", [], [], now, volume, muted, false, false);
    }
}
