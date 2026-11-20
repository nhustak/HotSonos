namespace HotSonos.Remote.Protocol.Tests;

public class RemoteShortcutTests
{
    [Theory]
    [InlineData("Space", false, RemoteShortcut.PlayPause)]
    [InlineData("MediaPlayPause", false, RemoteShortcut.PlayPause)]
    [InlineData("Left", false, RemoteShortcut.Previous)]
    [InlineData("MediaPrevious", false, RemoteShortcut.Previous)]
    [InlineData("Right", false, RemoteShortcut.Next)]
    [InlineData("MediaNext", false, RemoteShortcut.Next)]
    [InlineData("Up", false, RemoteShortcut.VolumeUp)]
    [InlineData("Plus", false, RemoteShortcut.VolumeUp)]
    [InlineData("Down", false, RemoteShortcut.VolumeDown)]
    [InlineData("Minus", false, RemoteShortcut.VolumeDown)]
    [InlineData("M", false, RemoteShortcut.Mute)]
    [InlineData("MediaMute", false, RemoteShortcut.Mute)]
    public void Keys_MapToThePlayerCommands(string key, bool shift, RemoteShortcut expected)
    {
        Assert.True(RemoteShortcuts.TryMatch(key, shift, typing: false, chord: false, out var shortcut));
        Assert.Equal(expected, shortcut);
    }

    [Fact]
    public void ShiftEquals_IsVolumeUp_AndEqualsAloneIsNot()
    {
        Assert.True(RemoteShortcuts.TryMatch("Equals", shift: true, typing: false, chord: false, out var up));
        Assert.Equal(RemoteShortcut.VolumeUp, up);
        Assert.False(RemoteShortcuts.TryMatch("Equals", shift: false, typing: false, chord: false, out _));
    }

    [Fact]
    public void Typing_KeepsTextKeys_AndStillTakesMediaKeys()
    {
        Assert.False(RemoteShortcuts.TryMatch("Space", false, typing: true, chord: false, out _));
        Assert.False(RemoteShortcuts.TryMatch("M", false, typing: true, chord: false, out _));
        Assert.False(RemoteShortcuts.TryMatch("Left", false, typing: true, chord: false, out _));
        Assert.False(RemoteShortcuts.TryMatch("Plus", false, typing: true, chord: false, out _));
        Assert.True(RemoteShortcuts.TryMatch("MediaPlayPause", false, typing: true, chord: false, out var play));
        Assert.Equal(RemoteShortcut.PlayPause, play);
        Assert.True(RemoteShortcuts.TryMatch("MediaNext", false, typing: true, chord: false, out _));
        Assert.True(RemoteShortcuts.TryMatch("MediaMute", false, typing: true, chord: false, out _));
    }

    [Fact]
    public void Chords_DoNotStealLetterKeys()
    {
        Assert.False(RemoteShortcuts.TryMatch("M", false, typing: false, chord: true, out _));
        Assert.False(RemoteShortcuts.TryMatch("Space", false, typing: false, chord: true, out _));
        Assert.True(RemoteShortcuts.TryMatch("MediaPrevious", false, typing: false, chord: true, out var previous));
        Assert.Equal(RemoteShortcut.Previous, previous);
    }
}

public class RemoteWindowTests
{
    [Fact]
    public void Column_FillsAPhoneAndCapsADesktopWindow()
    {
        Assert.Equal(0, RemoteWindow.Column(0, RemoteWindow.PlayerColumnMax));
        Assert.Equal(400, RemoteWindow.Column(400, RemoteWindow.PlayerColumnMax));
        Assert.Equal(RemoteWindow.PlayerColumnMax, RemoteWindow.Column(1400, RemoteWindow.PlayerColumnMax));
        Assert.Equal(640, RemoteWindow.Column(640, RemoteWindow.PageColumnMax));
        Assert.Equal(RemoteWindow.PageColumnMax, RemoteWindow.Column(1600, RemoteWindow.PageColumnMax));
    }

    [Fact]
    public void Tabs_ScrollOnAPhoneAndSpreadOnceTheyFit()
    {
        Assert.Equal(-1, RemoteWindow.TabButtonWidth(412, 8));
        Assert.Equal(-1, RemoteWindow.TabButtonWidth(600, 8));
        Assert.Equal(84, RemoteWindow.TabButtonWidth(680, 8));
        Assert.Equal(111, RemoteWindow.TabButtonWidth(900, 8));
        Assert.Equal(RemoteWindow.TabButtonMax, RemoteWindow.TabButtonWidth(1600, 8));
    }
}

public class FindPcMissedTests
{
    [Fact]
    public void Miss_TellsYouToTypeTheAddressUnderConnection()
    {
        Assert.Contains("No PC answered", RemoteFailureText.FindPcMissed, StringComparison.Ordinal);
        Assert.Contains("Chromebook", RemoteFailureText.FindPcMissed, StringComparison.Ordinal);
        Assert.Contains("42343", RemoteFailureText.FindPcMissed, StringComparison.Ordinal);
        Assert.Contains("Settings, then Connection / PC", RemoteFailureText.FindPcMissed, StringComparison.Ordinal);
    }
}
