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
    readonly RefreshView _refresh = new() { RefreshColor = Swatches.Accent };
    bool _suppressKeep;
    bool _loading;
    bool _loadQueued;
    string? _seenKey;

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

        var page = RemoteUi.Page(
            "Rooms",
            _status,
            RemoteUi.Section(
                PhoneSections.RoomsHouse,
                "House",
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
                RemoteUi.Button("Reboot all speakers", () => RebootAsync(null, true), danger: true)),
            RemoteUi.Section(PhoneSections.RoomsGroups, "Topology", _groups),
            RemoteUi.Section(PhoneSections.RoomsSpeakers, "Speakers", _members),
            RemoteUi.Section(PhoneSections.RoomsVolume, "Volume per room", _volumes));
        _refresh.Content = page;
        _refresh.Command = new Command(() => _ = LoadAsync());
        Content = _refresh;
    }

    protected override void OnStatus(RemoteStatusResponse? status)
    {
        var key = (status?.LiveGrouping ?? "") + "|" + (status?.NowPlaying?.State ?? "");
        if (_seenKey is not null && string.Equals(key, _seenKey, StringComparison.Ordinal))
            return;
        _ = LoadAsync();
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    async Task LoadAsync()
    {
        if (_loading)
        {
            _loadQueued = true;
            return;
        }

        _loading = true;
        try
        {
            await LoadCoreAsync();
            var live = AppServices.Live.Status;
            _seenKey = (live?.LiveGrouping ?? "") + "|" + (live?.NowPlaying?.State ?? "");
        }
        finally
        {
            _loading = false;
            _refresh.IsRefreshing = false;
            if (_loadQueued)
            {
                _loadQueued = false;
                _ = LoadAsync();
            }
        }
    }

    async Task LoadCoreAsync()
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

        ShowCards(value.Layout, value.Offline, value.Vanished);

        _members.Clear();
        foreach (var member in value.Members.Where(m => !m.Invisible))
        {
            var room = member.Room;
            var label = member.IsCoordinator ? room + " · coordinator" : room;
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
        SonosTopologySnapshot snap;
        IReadOnlyDictionary<string, string> play;
        try
        {
            snap = await house.RefreshTopologyAsync(CancellationToken.None);
            play = await house.ReadGroupPlayStatesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            snap = house.LastTopology ?? new SonosTopologySnapshot();
            play = new Dictionary<string, string>();
            if (snap.Members.Count == 0)
                _status.Text = ex.Message;
        }

        var cards = TopologyLayout.Map(snap.Members.Select(m => new TopologyMemberInput(
                m.RoomName, m.GroupId, m.IsCoordinator, m.Invisible, m.ChannelRole, m.ProductName)))
            .Select(card => new RemoteTopologyGroupDto(
                card.GroupId,
                card.CoordinatorRoom,
                card.Ungrouped,
                play.TryGetValue(card.GroupId, out var state) ? state : "Unknown",
                card.Rooms.Select(r => new RemoteTopologyRoomDto(r.Room, r.IsCoordinator, r.Folded)).ToList()))
            .ToList();
        if (cards.Count > 0)
            _status.Text = "Grouped now: " + string.Join("; ", cards.Select(DescribeCard))
                + "\n" + RemoteFailureText.PcRequired("Keep grouped, Daily regroup, and reboot");
        else
        {
            var picture = LiveGrouping.Map(house.Zones.Select(z =>
                new SpeakerMembership(z.RoomName, z.CoordinatorUuid, z.IsCoordinator)));
            _status.Text = picture.Groups.Count == 0
                ? (AppServices.Link.Problem ?? "No speakers yet.")
                : "Grouped now: " + picture.Summary + "\n" + RemoteFailureText.PcRequired("Keep grouped, Daily regroup, and reboot");
        }

        ShowCards(cards, [], snap.VanishedRooms);

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

    void ShowCards(
        IReadOnlyList<RemoteTopologyGroupDto>? layout,
        IReadOnlyList<string> offline,
        IReadOnlyList<string> vanished)
    {
        _groups.Clear();
        if (layout is null || layout.Count == 0)
        {
            _groups.Add(RemoteUi.Muted("No topology yet. Pull down to refresh."));
            return;
        }

        foreach (var card in layout)
            _groups.Add(Card(card));

        var gone = offline.Concat(vanished)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (gone.Count > 0)
            _groups.Add(RemoteUi.Muted("Offline: " + string.Join(", ", gone)));
    }

    static View Card(RemoteTopologyGroupDto card)
    {
        var title = card.Ungrouped
            ? card.CoordinatorRoom
            : string.Join(" + ", card.Rooms.Select(r => r.Room));
        var play = string.IsNullOrWhiteSpace(card.PlayState) ? "Unknown" : card.PlayState;
        var kind = card.Ungrouped
            ? "On its own · " + play
            : "Coordinator: " + card.CoordinatorRoom + " · " + play;
        var body = new VerticalStackLayout { Spacing = 2 };
        if (!card.Ungrouped)
        {
            foreach (var room in card.Rooms)
            {
                body.Add(RemoteUi.Muted(room.IsCoordinator ? room.Room + " · coordinator" : room.Room));
                foreach (var folded in room.Folded)
                    body.Add(FoldedLine(folded));
            }
        }
        else
        {
            foreach (var folded in card.Rooms.SelectMany(r => r.Folded))
                body.Add(FoldedLine(folded));
        }

        return RemoteUi.Card(
            new Label { Text = title, TextColor = Swatches.Ink, FontAttributes = FontAttributes.Bold, FontSize = 16 },
            RemoteUi.Muted(kind),
            body);
    }

    static Label FoldedLine(string folded) =>
        new()
        {
            Text = "    " + folded,
            TextColor = Swatches.Muted,
            FontSize = 13,
        };

    static string DescribeCard(RemoteTopologyGroupDto card)
    {
        if (card.Ungrouped)
            return card.CoordinatorRoom;
        var names = string.Join(" + ", card.Rooms.Select(r => r.Room));
        var folded = card.Rooms.SelectMany(r => r.Folded).ToList();
        return folded.Count == 0 ? names : names + " (+ " + string.Join(", ", folded) + ")";
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
