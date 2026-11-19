using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class OptionsPage : RemotePage
{
    readonly Label _status = RemoteUi.Muted("");
    readonly Switch _wake = new();
    readonly Entry _wakeTime = RemoteUi.Field("Wake HH:mm");
    readonly Entry _wakeRoom = RemoteUi.Field("Wake room, blank = active");
    readonly Switch _wakeFavorite = new();
    readonly Entry _wakeFavoriteName = RemoteUi.Field("Favorite name");
    readonly Entry _wakeStart = RemoteUi.Field("Start volume", Keyboard.Numeric);
    readonly Entry _wakeEnd = RemoteUi.Field("End volume", Keyboard.Numeric);
    readonly Switch _wakeExpand = new();
    readonly Switch _nightly = new();
    readonly Entry _nightlyTime = RemoteUi.Field("Nightly HH:mm");
    readonly Switch _nightlyShuffle = new();
    readonly Switch _genres = new();
    readonly Switch _gena = new();
    readonly Switch _exclude = new();
    readonly Switch _topUp = new();
    readonly Switch _continue = new();
    readonly Switch _spread = new();
    readonly Switch _master = new();
    readonly Entry _step = RemoteUi.Field("Volume step", Keyboard.Numeric);
    readonly Entry _level = RemoteUi.Field("Level %", Keyboard.Numeric);
    readonly Entry _queue = RemoteUi.Field("Shuffle queue", Keyboard.Numeric);
    readonly Entry _topUpSize = RemoteUi.Field("Top-up size", Keyboard.Numeric);
    readonly Entry _topUpAt = RemoteUi.Field("Top-up when remaining", Keyboard.Numeric);
    readonly Entry _history = RemoteUi.Field("History days", Keyboard.Numeric);
    readonly VerticalStackLayout _days = new();
    readonly VerticalStackLayout _logs = new();
    readonly Switch[] _day = new Switch[7];
    readonly ConnectionForm _connection = new();
    readonly ScrollView _scroll;

    public OptionsPage()
    {
        var names = new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
        var dayRow = new HorizontalStackLayout { Spacing = 6 };
        for (var i = 0; i < 7; i++)
        {
            _day[i] = new Switch();
            dayRow.Add(new VerticalStackLayout
            {
                Children =
                {
                    new Label { Text = names[i], TextColor = Swatches.Muted, HorizontalTextAlignment = TextAlignment.Center },
                    _day[i],
                },
            });
        }

        _days.Add(dayRow);
        _scroll = RemoteUi.Page(
            "More",
            RemoteUi.Muted("Connection"),
            _connection.Status,
            _connection.Host,
            _connection.Port,
            _connection.Pin,
            RemoteUi.Button("Find PC", FindPcAsync),
            RemoteUi.Button("Test and save", SaveConnectionAsync),
            RemoteUi.Muted("Link"),
            RemoteUi.ModePicker(),
            RemoteUi.Button("Forget PC", ForgetAsync, danger: true),
            RemoteUi.Button("Use speakers without the PC", SpeakersAsync),
            _status,
            RemoteUi.Button("Reload", LoadAsync),
            RemoteUi.Muted("Wake"),
            Labeled("Enabled", _wake),
            _wakeTime,
            _days,
            _wakeRoom,
            Labeled("Favorite instead of shuffle", _wakeFavorite),
            _wakeFavoriteName,
            _wakeStart,
            _wakeEnd,
            Labeled("Then expand to the house", _wakeExpand),
            RemoteUi.Button("Wake now", () => OnPc() ? Act(ct => AppServices.Client.WakeNowAsync(ct), _status) : Task.CompletedTask),
            RemoteUi.Button("Cancel wake", () => OnPc() ? Act(ct => AppServices.Client.CancelWakeAsync(ct), _status) : Task.CompletedTask),
            RemoteUi.Muted("Nightly"),
            Labeled("Re-sync nightly", _nightly),
            _nightlyTime,
            Labeled("Also reshuffle", _nightlyShuffle),
            RemoteUi.Muted("Playback"),
            _step,
            _level,
            _queue,
            _topUpSize,
            _topUpAt,
            _history,
            Labeled("Show genres", _genres),
            Labeled("Speaker push events", _gena),
            Labeled("Exclude recently played", _exclude),
            Labeled("Auto top-up", _topUp),
            Labeled("Continue into library shuffle", _continue),
            Labeled("Spread artists", _spread),
            Labeled("Write tags to the master copy", _master),
            RemoteUi.Button("Save settings", SaveAsync),
            RemoteUi.Button("Failure diagnostic", DiagnosticAsync),
            RemoteUi.Muted("Recent PC log"),
            _logs,
            RemoteUi.Muted("Hotkey capture, Quick Tag, Quick Play, the tray flyout, and Start with Windows stay on the PC."));
        Content = _scroll;
    }

    async Task FindPcAsync()
    {
        _connection.Status.Text = "Looking for HotSonos on this network…";
        try
        {
            var found = await PcFinder.FindAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            if (found is null)
            {
                _connection.Status.Text = "No PC answered. Type the address from the Phone window.";
                return;
            }

            _connection.Host.Text = found.Value.Host;
            _connection.Port.Text = found.Value.Port.ToString();
            _connection.Status.Text = "Found " + found.Value.Host + ". Enter the PIN, then Test and save.";
        }
        catch (Exception ex)
        {
            _connection.Status.Text = ex.Message;
        }
    }

    async Task SaveConnectionAsync()
    {
        var call = await _connection.ProbeAsync();
        if (call is not { Kind: RemoteCallKind.Ok })
            return;
        if (!_connection.Save())
            return;
        if (AppServices.Link.Preference == PhoneLinkPreference.Standalone)
            AppServices.Link.SetPreference(PhoneLinkPreference.Pc);
        await AppServices.Link.ResolveAsync(CancellationToken.None);
        await LoadAsync();
    }

    async Task ForgetAsync()
    {
        if (!await Confirm("Forget PC", "Clear the saved address and PIN on this phone?"))
            return;
        AppServices.Connection.Forget();
        AppServices.Link.SetPreference(PhoneLinkPreference.Pc);
        _connection.Load();
        _connection.Status.Text = "Forgotten. Enter the address and PIN from the PC Phone window.";
        await AppServices.Link.ResolveAsync(CancellationToken.None);
    }

    Task SpeakersAsync()
    {
        AppServices.Link.SetPreference(PhoneLinkPreference.Standalone);
        _ = AppServices.Link.ResolveAsync(CancellationToken.None);
        _connection.Status.Text = "Speakers only. The PC is not contacted.";
        return Task.CompletedTask;
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = _scroll.ScrollToAsync(0, 0, false);
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    async Task LoadAsync()
    {
        if (!AppServices.Link.DrivePc)
        {
            _logs.Clear();
            _status.Text = RemoteFailureText.PcRequired("Wake, nightly, and the other settings");
            return;
        }

        if (!AppServices.Link.OnPc)
        {
            _logs.Clear();
            _status.Text = AppServices.Link.Problem ?? "Cannot reach the PC.";
            return;
        }

        var call = await AppServices.Client.GetSettingsAsync(CancellationToken.None);
        if (call.Value is not { Ok: true } s)
        {
            _status.Text = call.Detail ?? "Could not load settings.";
            return;
        }

        _wake.IsToggled = s.WakeEnabled;
        _wakeTime.Text = Minutes(s.WakeMinutes);
        _wakeRoom.Text = s.WakeRoom ?? "";
        _wakeFavorite.IsToggled = s.WakeSource.Equals("Favorite", StringComparison.OrdinalIgnoreCase);
        _wakeFavoriteName.Text = s.WakeFavoriteName ?? "";
        _wakeStart.Text = s.WakeStartVolume.ToString();
        _wakeEnd.Text = s.WakeEndVolume.ToString();
        _wakeExpand.IsToggled = s.WakeExpandToHouse;
        for (var i = 0; i < 7; i++)
            _day[i].IsToggled = (s.WakeDaysMask & (1 << i)) != 0;
        _nightly.IsToggled = s.NightlyResetEnabled;
        _nightlyTime.Text = Minutes(s.NightlyResetMinutes);
        _nightlyShuffle.IsToggled = s.NightlyResetReshuffle;
        _genres.IsToggled = s.ShowGenres;
        _gena.IsToggled = s.GenaEnabled;
        _exclude.IsToggled = s.ShuffleExcludePlayed;
        _topUp.IsToggled = s.ShuffleAutoTopUp;
        _continue.IsToggled = s.ContinueAfterSpecial;
        _spread.IsToggled = s.ShuffleArtistSpread;
        _master.IsToggled = s.TagUpdateMaster;
        _step.Text = s.VolumeStep.ToString();
        _level.Text = s.LevelVolumePercent.ToString();
        _queue.Text = s.ShuffleQueueTracks.ToString();
        _topUpSize.Text = s.ShuffleTopUpTracks.ToString();
        _topUpAt.Text = s.ShuffleTopUpWhenRemaining.ToString();
        _history.Text = s.ShuffleHistoryDays.ToString();
        _status.Text = s.WakeActive ? "Wake is running. Shuffle from " + s.ControlShuffleLabel : "Shuffle from " + s.ControlShuffleLabel;

        var logs = await AppServices.Client.GetLogsAsync(CancellationToken.None);
        _logs.Clear();
        foreach (var line in (logs.Value?.Lines ?? []).TakeLast(12))
            _logs.Add(RemoteUi.Muted(line));
    }

    async Task SaveAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Those settings");
            return;
        }

        if (!TryMinutes(_wakeTime.Text, out var wake) || !TryMinutes(_nightlyTime.Text, out var nightly))
        {
            _status.Text = "Times are HH:mm.";
            return;
        }

        var mask = 0;
        for (var i = 0; i < 7; i++)
        {
            if (_day[i].IsToggled)
                mask |= 1 << i;
        }

        var body = new RemoteSettingsBody
        {
            WakeEnabled = _wake.IsToggled,
            WakeMinutes = wake,
            WakeDaysMask = mask,
            WakeRoom = _wakeRoom.Text ?? "",
            WakeSource = _wakeFavorite.IsToggled ? "Favorite" : "Shuffle",
            WakeFavoriteName = _wakeFavoriteName.Text ?? "",
            WakeStartVolume = Parse(_wakeStart.Text),
            WakeEndVolume = Parse(_wakeEnd.Text),
            WakeExpandToHouse = _wakeExpand.IsToggled,
            NightlyResetEnabled = _nightly.IsToggled,
            NightlyResetMinutes = nightly,
            NightlyResetReshuffle = _nightlyShuffle.IsToggled,
            ShowGenres = _genres.IsToggled,
            GenaEnabled = _gena.IsToggled,
            ShuffleExcludePlayed = _exclude.IsToggled,
            ShuffleAutoTopUp = _topUp.IsToggled,
            ContinueAfterSpecial = _continue.IsToggled,
            ShuffleArtistSpread = _spread.IsToggled,
            TagUpdateMaster = _master.IsToggled,
            VolumeStep = Parse(_step.Text),
            LevelVolumePercent = Parse(_level.Text),
            ShuffleQueueTracks = Parse(_queue.Text),
            ShuffleTopUpTracks = Parse(_topUpSize.Text),
            ShuffleTopUpWhenRemaining = Parse(_topUpAt.Text),
            ShuffleHistoryDays = Parse(_history.Text),
        };
        await Act(ct => AppServices.Client.SaveSettingsAsync(body, ct), _status);
    }

    async Task DiagnosticAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("The failure diagnostic");
            return;
        }

        if (!await Confirm("Failure diagnostic", "Run the PC diagnostic and save a report on the computer?"))
            return;
        await Act(ct => AppServices.Client.FailureDiagnosticAsync(ct), _status);
    }

    bool OnPc()
    {
        if (AppServices.Link.OnPc)
            return true;
        _status.Text = RemoteFailureText.PcRequired("Wake and nightly");
        return false;
    }

    static View Labeled(string text, Switch toggle) => new HorizontalStackLayout
    {
        Spacing = 8,
        Children =
        {
            toggle,
            new Label { Text = text, TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center },
        },
    };

    static string Minutes(int minutes) =>
        $"{Math.Clamp(minutes, 0, 1439) / 60:00}:{Math.Clamp(minutes, 0, 1439) % 60:00}";

    static bool TryMinutes(string? text, out int minutes)
    {
        minutes = 0;
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
            return false;
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;
        minutes = hour * 60 + minute;
        return true;
    }

    static int Parse(string? text) => int.TryParse(text, out var n) ? n : 0;
}
