using HotSonos.Core;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class SpeakersSettingsPage : RemotePage
{
    readonly Label _status = RemoteUi.Muted("");
    readonly Label _live = new() { TextColor = Swatches.Ink, FontSize = 16, FontAttributes = FontAttributes.Bold };
    readonly Label _drift = new() { Text = LiveGrouping.DriftMessage, TextColor = Swatches.Danger, IsVisible = false };
    readonly Button _regroup;
    readonly Label _daily = RemoteUi.Muted("Daily group preset");
    readonly Switch _allSpeakers = new();
    readonly VerticalStackLayout _roomChecks = new();
    readonly VerticalStackLayout _speakerPick = new() { Spacing = 6, IsVisible = false };
    readonly VerticalStackLayout _presets = new();
    readonly Entry _presetName = RemoteUi.Field("Group name");
    readonly Picker _rooms = new() { Title = "Choose a room", TextColor = Swatches.Ink, BackgroundColor = Swatches.Card };
    readonly Label _roomEmpty = RemoteUi.Muted("Waiting for rooms.");
    bool _suppressRoom;
    bool _suppressAll;
    string _roomKey = "";
    RemoteStatusResponse? _statusResponse;
    RemoteDailyGroupsResponse? _groups;

    public SpeakersSettingsPage()
    {
        _rooms.IsVisible = false;
        _rooms.SelectedIndexChanged += async (_, _) =>
        {
            if (_suppressRoom || _statusResponse is null)
                return;
            var index = _rooms.SelectedIndex;
            if (index < 0 || index >= _statusResponse.Groups.Count)
                return;
            var room = _statusResponse.Groups[index].Room;
            if (AppServices.Link.DrivePc)
                await Act(ct => AppServices.Client.SetRoomAsync(room, ct), _status);
            else
                AppServices.Link.SelectRoom(room);
        };
        _allSpeakers.Toggled += async (_, e) =>
        {
            if (_suppressAll)
                return;
            if (!AppServices.Link.DrivePc)
            {
                _suppressAll = true;
                _allSpeakers.IsToggled = !e.Value;
                _suppressAll = false;
                _status.Text = RemoteFailureText.PcRequired("Daily speaker groups");
                return;
            }

            await Act(ct => AppServices.Client.SelectDailyAsync(new RemoteDailySelectBody { All = e.Value }, ct), _status);
            await LoadDailyAsync();
        };

        _regroup = RemoteUi.Button("Regroup", RegroupDailyAsync, danger: true);
        _regroup.IsVisible = false;
        var saveGroup = RemoteUi.Button("Save checked rooms as a group", SavePresetAsync);
        _speakerPick.Add(_roomChecks);
        _speakerPick.Add(_presetName);
        _speakerPick.Add(saveGroup);

        Content = RemoteUi.Page(
            "Speakers & Daily group",
            RemoteUi.Back(),
            _status,
            _live,
            _drift,
            _regroup,
            RemoteUi.Muted("Room"),
            _rooms,
            _roomEmpty,
            RemoteUi.Card(
                RemoteUi.Muted("Daily group preset"),
                RemoteUi.Muted("Saved for shuffle, Fresh start, and Regroup. Not who is grouped right now."),
                _daily,
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { _allSpeakers, new Label { Text = "All speakers", TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center } },
                },
                _presets,
                _speakerPick),
            RemoteUi.Button("Refresh", LoadDailyAsync));
    }

    protected override void OnShown()
    {
        base.OnShown();
        ApplyRooms(AppServices.Live.Status);
        _ = LoadDailyAsync();
    }

    protected override void OnLinkChanged() => _ = LoadDailyAsync();

    protected override void OnStatus(RemoteStatusResponse? status)
    {
        ApplyLive(status);
        ApplyRooms(status);
    }

    void ApplyLive(RemoteStatusResponse? status)
    {
        var summary = status?.LiveGrouping;
        _live.IsVisible = !string.IsNullOrWhiteSpace(summary);
        _live.Text = string.IsNullOrWhiteSpace(summary) ? "" : "Grouped now: " + summary;
        var drift = status?.GroupedAsDaily == false;
        _drift.IsVisible = drift;
        _regroup.IsVisible = drift && AppServices.Link.DrivePc;
        if (!string.IsNullOrWhiteSpace(status?.DailyGroupPreset))
            _daily.Text = "Daily group preset: " + status.DailyGroupPreset;
    }

    async Task RegroupDailyAsync()
    {
        if (!PcOnly("Regroup"))
            return;
        if (!await Confirm("Regroup", "Join the Daily group preset? Playback pauses while the group is built. This does not start a new shuffle."))
            return;
        await Act(ct => AppServices.Client.RegroupAsync(null, ct), _status);
    }

    async Task LoadDailyAsync()
    {
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _daily.Text = RemoteFailureText.PcRequired("Daily speaker groups");
            _presets.Clear();
            _roomChecks.Clear();
            _speakerPick.IsVisible = false;
            if (!AppServices.Link.DrivePc)
                _status.Text = RemoteFailureText.PcRequired("Daily speaker groups");
            else if (_status.Text.Length == 0)
                _status.Text = AppServices.Link.Problem ?? "Cannot reach the PC.";
            return;
        }

        var call = await AppServices.Client.GetDailyGroupsAsync(CancellationToken.None);
        _groups = call.Value;
        if (_groups is null)
        {
            _status.Text = call.Detail ?? "Could not load the Daily group.";
            return;
        }

        _suppressAll = true;
        _allSpeakers.IsToggled = _groups.AllSpeakers;
        _suppressAll = false;
        ApplySpeakerChecklist(_groups.AllSpeakers);
        _daily.Text = "Daily group preset: " + (_groups.AllSpeakers
            ? "All speakers"
            : (_groups.ActivePreset ?? string.Join(", ", _groups.Rooms)));

        _presets.Clear();
        foreach (var preset in _groups.Presets)
        {
            var name = preset.Name;
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition() } };
            var use = RemoteUi.Button((preset.Active ? "On · " : "") + name, () => UsePresetAsync(name));
            var remove = RemoteUi.Button("Remove", () => RemovePresetAsync(name), danger: true);
            Grid.SetColumn(use, 0);
            Grid.SetColumn(remove, 1);
            row.Add(use);
            row.Add(remove);
            _presets.Add(row);
        }

        _roomChecks.Clear();
        var selected = new HashSet<string>(_groups.Rooms, StringComparer.OrdinalIgnoreCase);
        foreach (var room in _groups.KnownRooms)
        {
            var box = new CheckBox { IsChecked = selected.Contains(room), Color = Swatches.Accent };
            var label = new Label { Text = room, TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center };
            _roomChecks.Add(new HorizontalStackLayout { Spacing = 8, Children = { box, label } });
        }
    }

    async Task UsePresetAsync(string name)
    {
        if (!PcOnly("Daily speaker groups"))
            return;
        await Act(ct => AppServices.Client.SelectDailyAsync(new RemoteDailySelectBody { Preset = name, PresetOn = true }, ct), _status);
        await LoadDailyAsync();
    }

    async Task RemovePresetAsync(string name)
    {
        if (!PcOnly("Daily speaker groups"))
            return;
        if (!await Confirm("Remove group", $"Remove the Daily group {name}?"))
            return;
        await Act(ct => AppServices.Client.RemoveDailyAsync(name, ct), _status);
        await LoadDailyAsync();
    }

    async Task SavePresetAsync()
    {
        if (!PcOnly("Daily speaker groups"))
            return;
        var rooms = CheckedRooms();
        await Act(ct => AppServices.Client.SaveDailyAsync(_presetName.Text ?? "", rooms, ct), _status);
        _presetName.Text = "";
        await LoadDailyAsync();
    }

    void ApplySpeakerChecklist(bool allSpeakers) =>
        _speakerPick.IsVisible = DailyGroupLogic.ShowSpeakerChecklist(allSpeakers);

    List<string> CheckedRooms()
    {
        var rooms = new List<string>();
        foreach (var row in _roomChecks.Children.OfType<HorizontalStackLayout>())
        {
            var box = row.Children.OfType<CheckBox>().FirstOrDefault();
            var label = row.Children.OfType<Label>().FirstOrDefault();
            if (box?.IsChecked == true && !string.IsNullOrWhiteSpace(label?.Text))
                rooms.Add(label.Text);
        }

        return rooms;
    }

    bool PcOnly(string what)
    {
        if (AppServices.Link.DrivePc)
            return true;
        _status.Text = RemoteFailureText.PcRequired(what);
        return false;
    }

    void ApplyRooms(RemoteStatusResponse? status)
    {
        _statusResponse = status;
        var groups = status?.Groups ?? [];
        var key = string.Join("|", groups.Select(g => $"{g.Room}={g.Label}:{g.Members}"));
        if (key == _roomKey)
            return;
        _roomKey = key;
        _suppressRoom = true;
        _rooms.Items.Clear();
        foreach (var group in groups)
            _rooms.Items.Add(string.IsNullOrWhiteSpace(group.Label) ? group.Room : group.Label);
        var index = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            if (string.Equals(groups[i].Room, status?.ActiveRoom, StringComparison.OrdinalIgnoreCase))
                index = i;
        }

        _rooms.SelectedIndex = groups.Count == 0 ? -1 : index;
        _rooms.IsVisible = groups.Count > 0;
        _roomEmpty.IsVisible = groups.Count == 0;
        _roomEmpty.Text = AppServices.Link.DrivePc
            ? "No rooms from the PC yet."
            : "No speakers answered yet.";
        _suppressRoom = false;
    }
}
