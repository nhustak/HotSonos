namespace HotSonos.Remote.Views;

sealed class SettingsPage : RemotePage
{
    public SettingsPage()
    {
        Content = RemoteUi.Page(
            "Settings",
            RemoteUi.Muted("Connection, playback, and the house."),
            RemoteUi.Button("Connection / PC", () => Open("connection", () => new ConnectionSettingsPage())),
            RemoteUi.Button("Playback & Shuffle", () => Open("playback", () => new PlaybackSettingsPage())),
            RemoteUi.Button("Spotify links", () => Open("spotify", () => new SpotifySettingsPage())),
            RemoteUi.Button("Library", () => Open("library-settings", () => new LibrarySettingsPage())),
            RemoteUi.Button("Speakers & Daily group", () => Open("speakers", () => new SpeakersSettingsPage())),
            RemoteUi.Button("Wake & nightly", () => Open("wake", () => new WakeSettingsPage())),
            RemoteUi.Button("About", () => Open("about", () => new AboutSettingsPage())));
    }

    static Task Open(string key, Func<RemotePage> create)
    {
        AppShell.Push(key, create);
        return Task.CompletedTask;
    }
}
