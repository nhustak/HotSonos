using HotSonos.Core;
using HotSonos.Core.Models;
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
    readonly VerticalStackLayout _layoutButtons = new() { Spacing = 6 };
    readonly Entry _layoutName = RemoteUi.Field("Layout name");
    readonly VerticalStackLayout _layoutRooms = new() { Spacing = 6 };
    readonly Picker _layoutSub = new() { Title = "Sub in", TextColor = Swatches.Ink, BackgroundColor = Swatches.Card };
    readonly Label _layoutHint = RemoteUi.Muted("");
    string? _selectedLayoutId;
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
            RemoteUi.Section(PhoneSections.SpeakersRoom, "Room", _rooms, _roomEmpty),
            RemoteUi.Section(
                PhoneSections.SpeakersDaily,
                "Daily group preset",
                RemoteUi.Card(
                    RemoteUi.Muted("Saved for shuffle, Fresh start, and Regroup. Not who is grouped right now."),
                    _daily,
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children = { _allSpeakers, new Label { Text = "All speakers", TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center } },
                    },
                    _presets,
                    _speakerPick)),
            RemoteUi.Section(
                PhoneSections.SpeakersLayouts,
                "Speaker layouts",
                RemoteUi.Card(
                    RemoteUi.Muted("Moves the Sub and rebuilds the pair. Linked to the PC: layouts live there. Talking to the speakers directly: saved on this phone."),
                    _layoutHint,
                    _layoutButtons,
                    _layoutName,
                    _layoutRooms,
                    _layoutSub,
                    RemoteUi.Button("Save from current", SaveLayoutAsync),
                    RemoteUi.Button("Delete layout", DeleteLayoutAsync, danger: true))),
            RemoteUi.Button("Refresh", LoadAllAsync));
    }

    protected override void OnShown()
    {
        base.OnShown();
        ApplyRooms(AppServices.Live.Status);
        _ = LoadAllAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAllAsync();

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
        _status.Text = "Regrouping…";
        await Act(ct => AppServices.Client.RegroupAsync(null, ct), _status, announce: true, done: "Regrouped");
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
            _status.Text = PairingPrompt.Miss(call, "Could not load the Daily group.");
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

    async Task LoadAllAsync()
    {
        await LoadDailyAsync();
        await LoadLayoutsAsync();
    }

    async Task LoadLayoutsAsync()
    {
        _layoutButtons.Clear();
        _selectedLayoutId = null;
        if (AppServices.Link.DrivePc)
        {
            _layoutHint.Text = "Layouts saved on the PC.";
            if (!AppServices.Link.OnPc)
            {
                _layoutHint.Text = AppServices.Link.Problem ?? "Cannot reach the PC.";
                return;
            }

            var call = await AppServices.Client.GetLayoutsAsync(CancellationToken.None);
            var pcLayouts = call.Value;
            if (pcLayouts is null)
            {
                _layoutHint.Text = PairingPrompt.Miss(call, "Could not load layouts.");
                return;
            }

            RenderLayoutButtons(pcLayouts.Layouts.Select(l => (l.Id, l.Name, l.SubRoom)).ToList());
            FillLayoutRoomChecks(pcLayouts.KnownRooms, []);
            return;
        }

        _layoutHint.Text = "Saved on this phone.";
        try
        {
            await AppServices.Link.Speakers.RefreshTopologyAsync();
        }
        catch
        {
            // Discover if we have no topology yet.
            try { await AppServices.Link.Speakers.DiscoverAsync(); }
            catch { /* status below */ }
            try { await AppServices.Link.Speakers.RefreshTopologyAsync(); }
            catch (Exception ex)
            {
                _layoutHint.Text = ex.Message;
            }
        }

        var localLayouts = PhoneLayoutStore.Load();
        RenderLayoutButtons(localLayouts.Select(l => (l.Id, l.Name, SonosBonding.SubHostRoom(l))).ToList());
        var rooms = AppServices.Link.Speakers.LastTopology?.Members
            .Where(m => !m.Invisible)
            .Select(m => m.RoomName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        FillLayoutRoomChecks(rooms, []);
    }

    void RenderLayoutButtons(IReadOnlyList<(string Id, string Name, string? SubRoom)> layouts)
    {
        _layoutButtons.Clear();
        if (layouts.Count == 0)
        {
            _layoutButtons.Add(RemoteUi.Muted("None saved yet."));
            return;
        }

        foreach (var layout in layouts)
        {
            var id = layout.Id;
            var label = string.IsNullOrWhiteSpace(layout.SubRoom)
                ? layout.Name
                : $"{layout.Name} · Sub in {layout.SubRoom}";
            _layoutButtons.Add(RemoteUi.Button(label, () => ApplyLayoutAsync(id)));
        }
    }

    void FillLayoutRoomChecks(IReadOnlyList<string> rooms, IReadOnlyCollection<string> selected)
    {
        _layoutRooms.Clear();
        var pick = new List<string>();
        foreach (var room in rooms)
        {
            var box = new CheckBox { IsChecked = selected.Contains(room) || (selected.Count == 0 && rooms.Count <= 2), Color = Swatches.Accent };
            var label = new Label { Text = room, TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center };
            _layoutRooms.Add(new HorizontalStackLayout { Spacing = 8, Children = { box, label } });
            pick.Add(room);
        }

        _layoutSub.Items.Clear();
        foreach (var room in pick)
            _layoutSub.Items.Add(room);
        if (pick.Count > 0 && _layoutSub.SelectedIndex < 0)
            _layoutSub.SelectedIndex = 0;
    }

    List<string> CheckedLayoutRooms()
    {
        var rooms = new List<string>();
        foreach (var row in _layoutRooms.Children.OfType<HorizontalStackLayout>())
        {
            var box = row.Children.OfType<CheckBox>().FirstOrDefault();
            var label = row.Children.OfType<Label>().FirstOrDefault();
            if (box?.IsChecked == true && !string.IsNullOrWhiteSpace(label?.Text))
                rooms.Add(label.Text);
        }

        return rooms;
    }

    async Task ApplyLayoutAsync(string id)
    {
        _selectedLayoutId = id;
        if (AppServices.Link.DrivePc)
        {
            await Act(ct => AppServices.Client.ApplyLayoutAsync(id, ct), _status, announce: true);
            return;
        }

        var layout = PhoneLayoutStore.Load().FirstOrDefault(l => l.Id == id);
        if (layout is null)
        {
            _status.Text = "No such layout.";
            return;
        }

        _status.Text = "Applying…";
        try
        {
            var result = await AppServices.Link.Speakers.ApplyLayoutAsync(layout);
            var toast = result.Ok
                ? result.Message
                : string.IsNullOrWhiteSpace(result.FailedStep)
                    ? result.Message
                    : $"{result.FailedStep}: {result.Message}";
            _status.Text = toast;
            Toasts.Show(toast);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            Toasts.Show(ex.Message);
        }
    }

    async Task SaveLayoutAsync()
    {
        var name = _layoutName.Text?.Trim();
        var rooms = CheckedLayoutRooms();
        if (string.IsNullOrWhiteSpace(name) || rooms.Count == 0)
        {
            _status.Text = "Name the layout and check the rooms it affects.";
            return;
        }

        var subIn = _layoutSub.SelectedIndex >= 0 && _layoutSub.SelectedIndex < _layoutSub.Items.Count
            ? _layoutSub.Items[_layoutSub.SelectedIndex]?.ToString()
            : null;

        if (AppServices.Link.DrivePc)
        {
            await Act(ct => AppServices.Client.SaveLayoutAsync(new RemoteLayoutSaveBody
            {
                Name = name,
                Rooms = rooms,
                SubInRoom = subIn,
            }, ct), _status, announce: true);
            await LoadLayoutsAsync();
            return;
        }

        var snap = AppServices.Link.Speakers.LastTopology;
        if (snap is null || snap.Members.Count == 0)
        {
            try { snap = await AppServices.Link.Speakers.RefreshTopologyAsync(); }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
                return;
            }
        }

        if (snap is null || snap.Members.Count == 0)
        {
            _status.Text = "No topology yet.";
            return;
        }

        try
        {
            var layout = SonosBonding.Snapshot(snap, name, rooms);
            if (layout.Rooms.Count == 0)
            {
                _status.Text = "None of the checked rooms are on the network.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(subIn)
                && layout.Rooms.Any(r => string.Equals(r.RoomName, subIn, StringComparison.OrdinalIgnoreCase))
                && layout.Rooms.Any(r => !string.IsNullOrWhiteSpace(r.SubUuid) || ChannelMaps.HasSub(r.ChannelMapSet))
                && !string.Equals(SonosBonding.SubHostRoom(layout), subIn, StringComparison.OrdinalIgnoreCase))
            {
                layout = SonosBonding.RelocateSub(layout, subIn);
                layout.Name = name;
            }

            var list = PhoneLayoutStore.Load();
            list.Add(layout);
            PhoneLayoutStore.Save(list);
            _layoutName.Text = "";
            _status.Text = $"Saved {layout.Name}.";
            await LoadLayoutsAsync();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    async Task DeleteLayoutAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedLayoutId))
        {
            _status.Text = "Apply a layout first, or pick one after saving.";
            return;
        }

        if (!await Confirm("Delete layout", "Delete this speaker layout?"))
            return;

        if (AppServices.Link.DrivePc)
        {
            await Act(ct => AppServices.Client.DeleteLayoutAsync(_selectedLayoutId, ct), _status, announce: true);
            await LoadLayoutsAsync();
            return;
        }

        var list = PhoneLayoutStore.Load().Where(l => l.Id != _selectedLayoutId).ToList();
        PhoneLayoutStore.Save(list);
        _status.Text = "Deleted.";
        await LoadLayoutsAsync();
    }

}
