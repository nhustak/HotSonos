using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class WakeSettingsPage : RemotePage
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
    readonly Switch[] _day = new Switch[7];
    bool _ready;

    public WakeSettingsPage()
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

        Content = RemoteUi.Page(
            "Wake & nightly",
            RemoteUi.Back(),
            _status,
            RemoteUi.Button("Reload", LoadAsync),
            RemoteUi.Section(
                PhoneSections.Wake,
                "Wake",
                SettingsForm.Labeled("Enabled", _wake),
                _wakeTime,
                dayRow,
                _wakeRoom,
                SettingsForm.Labeled("Favorite instead of shuffle", _wakeFavorite),
                _wakeFavoriteName,
                _wakeStart,
                _wakeEnd,
                SettingsForm.Labeled("Then expand to the house", _wakeExpand),
                RemoteUi.Button("Wake now", () => OnPc() ? Act(ct => AppServices.Client.WakeNowAsync(ct), _status) : Task.CompletedTask),
                RemoteUi.Button("Cancel wake", () => OnPc() ? Act(ct => AppServices.Client.CancelWakeAsync(ct), _status) : Task.CompletedTask)),
            RemoteUi.Section(
                PhoneSections.Nightly,
                "Nightly",
                SettingsForm.Labeled("Re-sync nightly", _nightly),
                _nightlyTime,
                SettingsForm.Labeled("Also reshuffle", _nightlyShuffle)),
            RemoteUi.Button("Save wake and nightly", SaveAsync));
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    async Task LoadAsync()
    {
        _ready = false;
        if (!AppServices.Link.DrivePc)
        {
            _status.Text = RemoteFailureText.PcRequired("Wake, nightly, and the other settings");
            return;
        }

        if (!AppServices.Link.OnPc)
        {
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
        _wakeTime.Text = SettingsForm.Minutes(s.WakeMinutes);
        _wakeRoom.Text = s.WakeRoom ?? "";
        _wakeFavorite.IsToggled = s.WakeSource.Equals("Favorite", StringComparison.OrdinalIgnoreCase);
        _wakeFavoriteName.Text = s.WakeFavoriteName ?? "";
        _wakeStart.Text = s.WakeStartVolume.ToString();
        _wakeEnd.Text = s.WakeEndVolume.ToString();
        _wakeExpand.IsToggled = s.WakeExpandToHouse;
        for (var i = 0; i < 7; i++)
            _day[i].IsToggled = (s.WakeDaysMask & (1 << i)) != 0;
        _nightly.IsToggled = s.NightlyResetEnabled;
        _nightlyTime.Text = SettingsForm.Minutes(s.NightlyResetMinutes);
        _nightlyShuffle.IsToggled = s.NightlyResetReshuffle;
        _ready = true;
        _status.Text = s.WakeActive ? "Wake is running." : "Wake is not running.";
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

        if (!SettingsForm.TryMinutes(_wakeTime.Text, out var wake) || !SettingsForm.TryMinutes(_nightlyTime.Text, out var nightly))
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
            WakeStartVolume = SettingsForm.Parse(_wakeStart.Text),
            WakeEndVolume = SettingsForm.Parse(_wakeEnd.Text),
            WakeExpandToHouse = _wakeExpand.IsToggled,
            NightlyResetEnabled = _nightly.IsToggled,
            NightlyResetMinutes = nightly,
            NightlyResetReshuffle = _nightlyShuffle.IsToggled,
        };
        await Act(ct => AppServices.Client.SaveSettingsAsync(body, ct), _status);
    }

    bool OnPc()
    {
        if (AppServices.Link.OnPc)
            return true;
        _status.Text = RemoteFailureText.PcRequired("Wake and nightly");
        return false;
    }
}
