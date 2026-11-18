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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        var topo = await AppServices.Client.GetTopologyAsync(CancellationToken.None);
        var value = topo.Value;
        if (value is null)
        {
            _status.Text = topo.Detail ?? "No topology.";
            return;
        }

        _suppressKeep = true;
        _keep.IsToggled = value.KeepHouseGrouped;
        _suppressKeep = false;
        _status.Text = value.Error
            ?? $"Active {value.ActiveRoom ?? "—"} · preferred {value.PreferredCoordinator ?? "—"}";

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

    async Task RegroupAsync()
    {
        if (!await Confirm("Regroup", "Regroup the Daily speaker group under the preferred coordinator?"))
            return;
        await Act(ct => AppServices.Client.RegroupAsync(null, ct), _status);
        await LoadAsync();
    }

    async Task RegroupRoomAsync(string room)
    {
        if (!await Confirm("Coordinator", $"Make {room} the coordinator and regroup the Daily group?"))
            return;
        await Act(ct => AppServices.Client.RegroupAsync(room, ct), _status);
        await LoadAsync();
    }

    async Task RebootAsync(string? room, bool all)
    {
        var message = all
            ? "Restart every speaker? Music stops for about a minute."
            : $"Restart {room}? It drops off for about a minute.";
        if (!await Confirm("Reboot", message))
            return;
        await Act(ct => AppServices.Client.RebootAsync(room, null, all, ct), _status);
    }
}
