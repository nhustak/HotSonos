using HotSonos.Core;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class TopologyPage : RemotePage
{
    readonly Label _status = RemoteUi.Muted("");
    readonly Switch _keep = new();
    readonly VerticalStackLayout _groups = new();
    readonly VerticalStackLayout _members = new();
    readonly VerticalStackLayout _volumes = new();
    bool _suppressKeep;

    public TopologyPage()
    {
        _keep.Toggled += async (_, e) =>
        {
            if (_suppressKeep)
                return;
            if (!AppServices.Link.DrivePc)
            {
                _suppressKeep = true;
                _keep.IsToggled = !e.Value;
                _suppressKeep = false;
                _status.Text = RemoteFailureText.PcRequired("Keep house grouped");
                return;
            }

            await Act(ct => AppServices.Client.KeepGroupedAsync(e.Value, ct), _status);
        };

        Content = RemoteUi.Page(
            "Rooms",
            _status,
            new HorizontalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    _keep,
                    new Label { Text = "Keep house grouped", TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center },
                },
            },
            RemoteUi.Button("Refresh", LoadAsync),
            RemoteUi.Button("Regroup Daily group", RegroupAsync, danger: true),
            RemoteUi.Button("Reboot all speakers", () => RebootAsync(null, true), danger: true),
            RemoteUi.Muted("Groups"),
            _groups,
            RemoteUi.Muted("Speakers"),
            _members,
            RemoteUi.Muted("Volume per room"),
            _volumes);
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    async Task LoadAsync()
    {
        if (!AppServices.Link.DrivePc)
        {
            await LoadSpeakersAsync();
            return;
        }

        if (!AppServices.Link.OnPc)
        {
            _groups.Clear();
            _members.Clear();
            _volumes.Clear();
            _groups.Add(RemoteUi.NeedsPc("Rooms on the PC"));
            _status.Text = AppServices.Link.Problem ?? "Cannot reach the PC.";
            return;
        }

        var topo = await AppServices.Client.GetTopologyAsync(CancellationToken.None);
        var value = topo.Value;
        if (value is null)
        {
            _status.Text = topo.Detail ?? "No topology.";
            return;
        }

        _keep.IsEnabled = true;
        _suppressKeep = true;
        _keep.IsToggled = value.KeepHouseGrouped;
        _suppressKeep = false;
        _status.Text = DescribeTopology(value);

        _groups.Clear();
        foreach (var group in value.Groups)
            _groups.Add(RemoteUi.Muted($"{group.Label} · {group.Members}"));

        _members.Clear();
        foreach (var member in value.Members)
        {
            var room = member.Room;
            var label = member.Invisible ? $"{room} ({member.Role ?? member.Product ?? "bonded"})" : room;
            if (member.IsCoordinator && !member.Invisible)
                label += " · coordinator";
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition(), new ColumnDefinition() } };
            var name = RemoteUi.Muted(label);
            var lead = RemoteUi.Button("Lead", () => RegroupRoomAsync(room));
            var reboot = RemoteUi.Button("Reboot", () => RebootAsync(room, false), danger: true);
            Grid.SetColumn(name, 0);
            Grid.SetColumn(lead, 1);
            Grid.SetColumn(reboot, 2);
            row.Add(name);
            row.Add(lead);
            row.Add(reboot);
            _members.Add(row);
        }

        var volumes = await AppServices.Client.GetVolumesAsync(CancellationToken.None);
        _volumes.Clear();
        foreach (var speaker in volumes.Value?.Speakers ?? [])
        {
            var room = speaker.Room;
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = speaker.Volume };
            slider.DragCompleted += async (_, _) =>
                await Act(ct => AppServices.Client.SetRoomVolumeAsync(room, null, (int)slider.Value, null, ct), _status);
            var mute = RemoteUi.Button(speaker.Muted ? "Unmute" : "Mute", () =>
                Act(ct => AppServices.Client.SetRoomVolumeAsync(room, null, null, !speaker.Muted, ct), _status));
            _volumes.Add(RemoteUi.Card(RemoteUi.Muted($"{room} · {speaker.Volume}%{(speaker.Muted ? " muted" : "")}{(speaker.Reachable ? "" : " · offline")}"), slider, mute));
        }
    }

    async Task LoadSpeakersAsync()
    {
        var house = AppServices.Link.Speakers;
        var picture = LiveGrouping.Map(house.Zones.Select(z =>
            new SpeakerMembership(z.RoomName, z.CoordinatorUuid, z.IsCoordinator)));
        _status.Text = picture.Groups.Count == 0
            ? (AppServices.Link.Problem ?? "No speakers yet.")
            : "Grouped now: " + picture.Summary + "\n" + RemoteFailureText.PcRequired("Keep grouped, Daily regroup, and reboot");

        _groups.Clear();
        foreach (var group in picture.Groups)
            _groups.Add(RemoteUi.Muted(group.Rooms.Count > 1 && picture.Groups.Count == 1 ? LiveGrouping.AllSpeakers : group.Label));

        _members.Clear();
        foreach (var zone in house.Zones)
        {
            var room = zone.RoomName;
            var label = zone.IsCoordinator ? room + " · lead" : room;
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition(), new ColumnDefinition() } };
            var lead = RemoteUi.Button("Lead", () => LeadAsync(room));
            var leave = RemoteUi.Button("Leave", () => LeaveAsync(room));
            var name = RemoteUi.Muted(label);
            Grid.SetColumn(name, 0);
            Grid.SetColumn(lead, 1);
            Grid.SetColumn(leave, 2);
            row.Add(name);
            row.Add(lead);
            row.Add(leave);
            _members.Add(row);
        }

        _volumes.Clear();
        var volumes = await house.ReadVolumesAsync(CancellationToken.None);
        foreach (var speaker in volumes)
        {
            var ip = speaker.Ip;
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = speaker.Volume };
            slider.DragCompleted += async (_, _) =>
            {
                try { await house.SetVolumeAsync(ip, (int)slider.Value, CancellationToken.None); _status.Text = "Done"; }
                catch (Exception ex) { _status.Text = ex.Message; }
            };
            var mute = RemoteUi.Button(speaker.Muted ? "Unmute" : "Mute", async () =>
            {
                try { await house.SetMuteAsync(ip, !speaker.Muted, CancellationToken.None); _status.Text = "Done"; await LoadAsync(); }
                catch (Exception ex) { _status.Text = ex.Message; }
            });
            _volumes.Add(RemoteUi.Card(
                RemoteUi.Muted($"{speaker.Room} · {speaker.Volume}%{(speaker.Muted ? " muted" : "")}{(speaker.Reachable ? "" : " · offline")}"),
                slider,
                mute));
        }
    }

    static string DescribeTopology(RemoteTopologyResponse value)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(value.LiveGrouping))
            lines.Add("Grouped now: " + value.LiveGrouping);
        if (!string.IsNullOrWhiteSpace(value.DailyGroupPreset))
            lines.Add("Daily group preset: " + value.DailyGroupPreset);
        if (value.GroupedAsDaily == false)
            lines.Add(LiveGrouping.DriftMessage);
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(value.Error))
            lines.Add(value.Error);
        if (lines.Count == 0)
            lines.Add($"Active {value.ActiveRoom ?? "—"} · preferred {value.PreferredCoordinator ?? "—"}");
        return string.Join("\n", lines);
    }

    async Task LeadAsync(string room)
    {
        if (!await Confirm("Coordinator", $"Make {room} the leader of its current group?"))
            return;
        try
        {
            await AppServices.Link.Speakers.SetCoordinatorAsync(room, wholeHouse: false, CancellationToken.None);
            _status.Text = room + " is leading its group.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }

        await LoadAsync();
    }

    async Task LeaveAsync(string room)
    {
        if (!await Confirm("Leave group", $"{room} will play on its own."))
            return;
        try
        {
            await AppServices.Link.Speakers.LeaveGroupAsync(room, CancellationToken.None);
            _status.Text = room + " left the group.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }

        await LoadAsync();
    }

    async Task RegroupAsync()
    {
        if (!AppServices.Link.DrivePc)
        {
            var room = AppServices.Link.Speakers.ActiveZone?.RoomName;
            if (room is null)
            {
                _status.Text = "No speakers yet.";
                return;
            }

            if (!await Confirm("Group all", $"Join every room to {room}?"))
                return;
            try
            {
                await AppServices.Link.Speakers.SetCoordinatorAsync(room, wholeHouse: true, CancellationToken.None);
                _status.Text = "Grouped under " + room + ".";
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
            }

            await LoadAsync();
            return;
        }

        if (!await Confirm("Regroup", "Regroup the Daily speaker group under the preferred coordinator?"))
            return;
        await Act(ct => AppServices.Client.RegroupAsync(null, ct), _status);
        await LoadAsync();
    }

    async Task RegroupRoomAsync(string room)
    {
        if (!AppServices.Link.DrivePc)
        {
            await LeadAsync(room);
            return;
        }

        if (!await Confirm("Coordinator", $"Make {room} the coordinator and regroup the Daily group?"))
            return;
        await Act(ct => AppServices.Client.RegroupAsync(room, ct), _status);
        await LoadAsync();
    }

    async Task RebootAsync(string? room, bool all)
    {
        if (!AppServices.Link.DrivePc)
        {
            _status.Text = RemoteFailureText.PcRequired("Reboot");
            return;
        }

        var message = all
            ? "Restart every speaker? Music stops for about a minute."
            : $"Restart {room}? It drops off for about a minute.";
        if (!await Confirm("Reboot", message))
            return;
        await Act(ct => AppServices.Client.RebootAsync(room, null, all, ct), _status);
    }
}
