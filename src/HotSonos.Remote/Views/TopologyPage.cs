using HotSonos.Core;
using HotSonos.Core.Models;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class TopologyPage : RemotePage
{
    readonly Label _status = RemoteUi.Muted("");
    readonly Switch _keep = new();
    readonly VerticalStackLayout _groups = new();
    readonly VerticalStackLayout _volumes = new();
    readonly Dictionary<string, Label> _playing = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<RemoteTopologyGroupDto>? _layout;
    readonly RefreshView _refresh = new() { RefreshColor = Swatches.Accent };
    bool _suppressKeep;
    bool _loading;
    bool _loadQueued;
    int _volumeRows;
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
            RemoteUi.Section(PhoneSections.RoomsVolume, "Rooms", _volumes));
        _refresh.Content = page;
        _refresh.Command = new Command(() => _ = LoadAsync());
        Content = _refresh;
    }

    protected override void OnStatus(RemoteStatusResponse? status)
    {
        PaintPlaying(status);
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
            _playing.Clear();
            _volumes.Clear();
            _layout = null;
            _groups.Add(RemoteUi.NeedsPc("Rooms on the PC"));
            _status.Text = AppServices.Link.Problem ?? "Cannot reach the PC.";
            return;
        }

        var topo = await AppServices.Client.GetTopologyAsync(CancellationToken.None);
        var value = topo.Value;
        if (value is null)
        {
            _status.Text = topo.Detail ?? "No topology.";
            ShowCards(null, [], []);
            // Room rows come from /api/volumes, which does not need the topology payload.
            await LoadSpeakerVolumesAsync();
            return;
        }

        _keep.IsEnabled = true;
        _suppressKeep = true;
        _keep.IsToggled = value.KeepHouseGrouped;
        _suppressKeep = false;
        _status.Text = DescribeTopology(value);

        ShowCards(value.Layout, value.Offline, value.Vanished);
        await LoadSpeakerVolumesAsync();
    }

    async Task LoadSpeakerVolumesAsync()
    {
        var volumes = await AppServices.Client.GetVolumesAsync(CancellationToken.None);
        var speakers = volumes.Value?.Speakers ?? [];
        var generation = ++_volumeRows;
        var step = await VolumeStepAsync();
        _playing.Clear();
        _volumes.Clear();
        foreach (var speaker in speakers)
        {
            var room = speaker.Room;
            var row = VolumeLine.Room(
                room,
                speaker.Volume,
                speaker.Muted,
                speaker.Reachable,
                step,
                generation,
                () => _volumeRows,
                percent => Act(ct => AppServices.Client.SetRoomVolumeAsync(room, null, percent, null, ct), _status),
                mute => Act(ct => AppServices.Client.SetRoomVolumeAsync(room, null, null, mute, ct), _status),
                () => HoldRoomAsync(room));
            _playing[room] = row.Playing;
            _volumes.Add(row.View);
        }

        PaintPlaying(AppServices.Live.Status);
    }

    async Task<int> VolumeStepAsync()
    {
        if (!AppServices.Link.DrivePc)
            return PhoneVolume.SpeakerStep;
        var call = await AppServices.Client.GetSettingsAsync(CancellationToken.None).ConfigureAwait(true);
        return call.Value is { Ok: true } settings
            ? PhoneVolume.Normalize(settings.VolumeStep)
            : PhoneVolume.SpeakerStep;
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

        var volumes = await house.ReadVolumesAsync(CancellationToken.None);
        var step = await VolumeStepAsync();
        var generation = ++_volumeRows;
        _playing.Clear();
        _volumes.Clear();
        foreach (var speaker in volumes)
        {
            var ip = speaker.Ip;
            var room = speaker.Room;
            var row = VolumeLine.Room(
                room,
                speaker.Volume,
                speaker.Muted,
                speaker.Reachable,
                step,
                generation,
                () => _volumeRows,
                async percent =>
                {
                    try
                    {
                        await house.SetVolumeAsync(ip, percent, CancellationToken.None).ConfigureAwait(true);
                        _status.Text = "Done";
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _status.Text = ex.Message;
                        return false;
                    }
                },
                async mute =>
                {
                    try
                    {
                        await house.SetMuteAsync(ip, mute, CancellationToken.None).ConfigureAwait(true);
                        _status.Text = "Done";
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _status.Text = ex.Message;
                        return false;
                    }
                },
                () => HoldRoomAsync(room));
            _playing[room] = row.Playing;
            _volumes.Add(row.View);
        }

        PaintPlaying(AppServices.Live.Status);
    }

    void ShowCards(
        IReadOnlyList<RemoteTopologyGroupDto>? layout,
        IReadOnlyList<string> offline,
        IReadOnlyList<string> vanished)
    {
        _layout = layout;
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

    void PaintPlaying(RemoteStatusResponse? status)
    {
        var title = status?.NowPlaying?.Title;
        var artist = status?.NowPlaying?.Artist;
        var active = status?.ActiveRoom;
        foreach (var (room, label) in _playing)
        {
            var line = RoomNowPlaying.Line(room, _layout, active, title, artist);
            label.Text = line ?? "";
            label.IsVisible = line is not null;
        }
    }

    async Task HoldRoomAsync(string room)
    {
        var actions = RoomRowActions.For(AppServices.Link.DrivePc);
        var pick = await DisplayActionSheet(room, "Cancel", null, actions.ToArray());
        if (pick == RoomRowActions.Coordinator)
            await RegroupRoomAsync(room);
        else if (pick == RoomRowActions.Reboot)
            await RebootAsync(room, false);
        else if (pick == RoomRowActions.Leave)
            await LeaveAsync(room);
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
