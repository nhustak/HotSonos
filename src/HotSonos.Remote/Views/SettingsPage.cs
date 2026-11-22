namespace HotSonos.Remote.Views;

sealed class SettingsPage : RemotePage
{
    public SettingsPage()
    {
#if ANDROID
        var media = new Switch { IsToggled = MediaControls.IsEnabled };
        media.Toggled += (_, e) => MediaControls.SetEnabled(e.Value);
        SemanticProperties.SetDescription(media, "Show media controls (watch, lock screen)");
#endif
        Content = RemoteUi.Page(
            "Settings",
            RemoteUi.Muted("Connection, playback, and the house."),
#if ANDROID
            MediaToggle(media),
            RemoteUi.Muted("Now playing on a paired watch and the lock screen. Play, pause, skip, and volume follow the active room."),
#endif
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

#if ANDROID
    static View MediaToggle(Switch toggle)
    {
        var label = new Label
        {
            Text = "Show media controls (watch, lock screen)",
            TextColor = Swatches.Ink,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        var grid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
        };
        grid.Add(toggle);
        Grid.SetColumn(label, 1);
        grid.Add(label);
        return grid;
    }
#endif
}
