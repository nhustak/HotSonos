using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class PlaybackSettingsPage : RemotePage
{
    readonly Label _status = RemoteUi.Muted("");
    readonly VerticalStackLayout _from = new();
    readonly Button _speakerShuffle;
    readonly Switch _genres = new();
    readonly Switch _gena = new();
    readonly Switch _exclude = new();
    readonly Switch _topUp = new();
    readonly Switch _continue = new();
    readonly Switch _spread = new();
    readonly Entry _step = RemoteUi.Field("Volume step", Keyboard.Numeric);
    readonly Entry _level = RemoteUi.Field("Level %", Keyboard.Numeric);
    readonly Entry _queue = RemoteUi.Field("Shuffle queue", Keyboard.Numeric);
    readonly Entry _topUpSize = RemoteUi.Field("Top-up size", Keyboard.Numeric);
    readonly Entry _topUpAt = RemoteUi.Field("Top-up when remaining", Keyboard.Numeric);
    readonly Entry _history = RemoteUi.Field("History days", Keyboard.Numeric);
    bool _ready;

    public PlaybackSettingsPage()
    {
        _speakerShuffle = RemoteUi.Button("Shuffle speaker library", ShuffleLibraryAsync);
        Content = RemoteUi.Page(
            "Playback & Shuffle",
            RemoteUi.Back(),
            _status,
            RemoteUi.Section(
                PhoneSections.PlaybackShuffle,
                "Shuffle",
                _speakerShuffle,
                RemoteUi.Muted("Start shuffle from"),
                _from,
                RemoteUi.Button("Start shuffle", () => PcOnly("HotSonos shuffle") ? Act(ct => AppServices.Client.StartShuffleAsync(ct), _status) : Task.CompletedTask),
                RemoteUi.Button("Daily shuffle", () => PcOnly("Daily shuffle") ? Act(ct => AppServices.Client.PlayDailyAsync(ct), _status) : Task.CompletedTask),
                RemoteUi.Button("Fresh start", FreshStartAsync, danger: true),
                RemoteUi.Button("Resume shuffle", () => PcOnly("Resume shuffle") ? Act(ct => AppServices.Client.ResumeShuffleAsync(ct), _status) : Task.CompletedTask)),
            RemoteUi.Section(
                PhoneSections.PlaybackOptions,
                "Playback options",
                _step,
                _level,
                _queue,
                _topUpSize,
                _topUpAt,
                _history,
                SettingsForm.Labeled("Show genres", _genres),
                SettingsForm.Labeled("Speaker push events", _gena),
                SettingsForm.Labeled("Exclude recently played", _exclude),
                SettingsForm.Labeled("Auto top-up", _topUp),
                SettingsForm.Labeled("Continue into library shuffle", _continue),
                SettingsForm.Labeled("Spread artists", _spread),
                RemoteUi.Button("Save playback settings", SaveAsync)),
            RemoteUi.Button("Refresh", LoadAsync));
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    async Task ShuffleLibraryAsync()
    {
        if (AppServices.Link.DrivePc)
        {
            if (!PcOnly("HotSonos shuffle"))
                return;
            await Act(ct => AppServices.Client.StartShuffleAsync(ct), _status);
            return;
        }

        _status.Text = "Picking a random sample from the speaker library…";
        await Speaker((speaker, ct) => speaker.PlayRandomLibrarySampleAsync(40, ct), _status);
    }

    async Task LoadAsync()
    {
        _speakerShuffle.IsVisible = !AppServices.Link.DrivePc;
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _from.Clear();
            _from.Add(RemoteUi.NeedsPc("HotSonos shuffle (play history, tags, genres, folders)"));
            _ready = false;
            _status.Text = AppServices.Link.DrivePc
                ? (AppServices.Link.Problem ?? "Cannot reach the PC.")
                : RemoteFailureText.PcRequired("HotSonos shuffle");
            return;
        }

        var settingsOk = await LoadSettingsAsync();
        var sources = await AppServices.Client.GetSourcesAsync(CancellationToken.None);
        _from.Clear();
        if (sources.Value is not { } src)
        {
            _status.Text = sources.Detail ?? "Could not load play sources.";
            return;
        }

        if (settingsOk)
            _status.Text = "Shuffle from " + src.ControlSourceLabel;
        _from.Add(RemoteUi.Button("All · Music Library", () => SetSourceAsync("all")));
        foreach (var tag in src.Tags)
        {
            var key = tag.Key;
            _from.Add(RemoteUi.Button("Tag · " + tag.Label, () => SetSourceAsync("tag:" + key)));
        }

        foreach (var genre in src.Genres)
        {
            var name = genre.Name;
            _from.Add(RemoteUi.Button("Genre · " + name, () => SetSourceAsync("genre:" + name)));
        }

        foreach (var folder in src.Folders)
        {
            var path = folder.Path;
            _from.Add(RemoteUi.Button("Folder · " + folder.Name, () => SetSourceAsync("folder:" + path)));
        }
    }

    async Task<bool> LoadSettingsAsync()
    {
        _ready = false;
        var call = await AppServices.Client.GetSettingsAsync(CancellationToken.None);
        if (call.Value is not { Ok: true } s)
        {
            _status.Text = call.Detail ?? "Could not load settings.";
            return false;
        }

        _genres.IsToggled = s.ShowGenres;
        _gena.IsToggled = s.GenaEnabled;
        _exclude.IsToggled = s.ShuffleExcludePlayed;
        _topUp.IsToggled = s.ShuffleAutoTopUp;
        _continue.IsToggled = s.ContinueAfterSpecial;
        _spread.IsToggled = s.ShuffleArtistSpread;
        _step.Text = s.VolumeStep.ToString();
        _level.Text = s.LevelVolumePercent.ToString();
        _queue.Text = s.ShuffleQueueTracks.ToString();
        _topUpSize.Text = s.ShuffleTopUpTracks.ToString();
        _topUpAt.Text = s.ShuffleTopUpWhenRemaining.ToString();
        _history.Text = s.ShuffleHistoryDays.ToString();
        _ready = true;
        return true;
    }

    async Task SetSourceAsync(string token)
    {
        if (!PcOnly("HotSonos shuffle"))
            return;
        await Act(ct => AppServices.Client.SetShuffleSourceAsync(token, ct), _status);
        await LoadAsync();
    }

    async Task FreshStartAsync()
    {
        if (!PcOnly("Fresh start"))
            return;
        if (!await Confirm("Fresh start", "Re-discover speakers, regroup the Daily group, and start a new shuffle?"))
            return;
        await Act(ct => AppServices.Client.FreshStartAsync(ct), _status);
    }

    async Task SaveAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Those settings");
            return;
        }

        if (!_ready)
        {
            _status.Text = "Settings are still loading.";
            return;
        }

        var body = new RemoteSettingsBody
        {
            ShowGenres = _genres.IsToggled,
            GenaEnabled = _gena.IsToggled,
            ShuffleExcludePlayed = _exclude.IsToggled,
            ShuffleAutoTopUp = _topUp.IsToggled,
            ContinueAfterSpecial = _continue.IsToggled,
            ShuffleArtistSpread = _spread.IsToggled,
            VolumeStep = SettingsForm.Parse(_step.Text),
            LevelVolumePercent = SettingsForm.Parse(_level.Text),
            ShuffleQueueTracks = SettingsForm.Parse(_queue.Text),
            ShuffleTopUpTracks = SettingsForm.Parse(_topUpSize.Text),
            ShuffleTopUpWhenRemaining = SettingsForm.Parse(_topUpAt.Text),
            ShuffleHistoryDays = SettingsForm.Parse(_history.Text),
        };
        await Act(ct => AppServices.Client.SaveSettingsAsync(body, ct), _status);
    }

    bool PcOnly(string what)
    {
        if (AppServices.Link.DrivePc)
            return true;
        _status.Text = RemoteFailureText.PcRequired(what);
        return false;
    }
}
