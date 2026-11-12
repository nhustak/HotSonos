using System.Globalization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>
/// Phone remote for the tray app. One activity, setup then the remote.
/// Polls only while resumed. Hardware volume keys are consumed on this screen.
/// </summary>
[Activity(
    Label = "HotSonos",
    MainLauncher = true,
    Exported = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    Theme = "@android:style/Theme.Material.NoActionBar",
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    const string PrefName = "hotsonos";
    const string PrefHost = "host";
    const string PrefPort = "port";
    const string PrefPin = "pin";

    readonly List<RemoteGroupDto> _rooms = [];
    readonly List<CancellationTokenSource> _oldSessions = [];

    RemoteClient _client = null!;
    CancellationTokenSource _alive = null!;
    CancellationTokenSource? _session;
    DialogVolumeKeys _dialogKeys = null!;

    ScreenMode _mode;
    int _generation;
    int _slow;
    bool _resumed;
    bool _draggingVolume;
    bool _reachError;
    bool _roomSuppress;
    string _roomKey = "";
    string? _spinnerRoom;
    string? _postedRoom;
    string? _artRevision;
    string? _artInFlight;
    Bitmap? _artBitmap;
    RemoteStatusResponse? _status;
    RemoteSourcesResponse? _sources;
    AlertDialog? _dialog;

    View _setupView = null!;
    View _remoteView = null!;
    EditText _hostBox = null!;
    EditText _portBox = null!;
    EditText _pinBox = null!;
    TextView _setupError = null!;
    Button _back = null!;
    TextView _banner = null!;
    LinearLayout _wakeBar = null!;
    ImageView _art = null!;
    TextView _title = null!;
    TextView _artist = null!;
    TextView _album = null!;
    TextView _meta = null!;
    Button _play = null!;
    Button _mute = null!;
    Button _daily = null!;
    TextView _volumeLabel = null!;
    SeekBar _volume = null!;
    Spinner _roomsSpinner = null!;
    LinearLayout _slotHost = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Color.ParseColor("#121212")));

        _client = new RemoteClient();
        _alive = new CancellationTokenSource();
        _dialogKeys = new DialogVolumeKeys(this);

        var root = new FrameLayout(this);
        _setupView = BuildSetup();
        _remoteView = BuildRemote();
        root.AddView(_remoteView);
        root.AddView(_setupView);
        SetContentView(root);
        WireEvents();
        LoadSavedConnection();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _resumed = true;
        if (_mode == ScreenMode.Remote)
            StartSession();
    }

    protected override void OnPause()
    {
        _resumed = false;
        CancelSession();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _resumed = false;
        CancelSession();
        try { _alive.Cancel(); } catch (ObjectDisposedException) { }
        DismissDialog();
        _art.SetImageBitmap(null);
        _artBitmap?.Dispose();
        _artBitmap = null;
        _client.Dispose();
        foreach (var session in _oldSessions)
            session.Dispose();
        _alive.Dispose();
        base.OnDestroy();
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (_mode == ScreenMode.Remote && _dialog?.IsShowing != true && e is not null
            && e.KeyCode is Keycode.VolumeUp or Keycode.VolumeDown)
        {
            if (e.Action == KeyEventActions.Down)
            {
                var direction = e.KeyCode == Keycode.VolumeUp ? 1 : -1;
                _ = SendFast(ct => _client.StepVolumeAsync(direction, ct));
            }

            return true;
        }

        return base.DispatchKeyEvent(e);
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (ConsumeVolume(keyCode))
            return true;
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        if (ConsumeVolume(keyCode))
            return true;
        return base.OnKeyUp(keyCode, e);
    }

    bool ConsumeVolume(Keycode keyCode) =>
        _mode == ScreenMode.Remote && keyCode is Keycode.VolumeUp or Keycode.VolumeDown;

    void LoadSavedConnection()
    {
        var prefs = Prefs();
        _hostBox.Text = prefs.GetString(PrefHost, "") ?? "";
        var port = prefs.GetString(PrefPort, null);
        _portBox.Text = string.IsNullOrWhiteSpace(port)
            ? RemoteApi.DefaultPort.ToString(CultureInfo.InvariantCulture)
            : port;
        _pinBox.Text = prefs.GetString(PrefPin, "") ?? "";

        if (!TryApplySaved())
            ShowSetup(null, allowBack: false);
        else
            ShowRemote();
    }

    bool TryApplySaved()
    {
        var prefs = Prefs();
        var pin = (prefs.GetString(PrefPin, "") ?? "").Trim();
        if (!RemoteNet.TryParseAddress(prefs.GetString(PrefHost, ""), prefs.GetString(PrefPort, ""), out var host, out var port, out _)
            || !RemoteNet.IsSixDigitPin(pin))
            return false;

        _client.Apply(host, port, pin);
        _hostBox.Text = host;
        _portBox.Text = port.ToString(CultureInfo.InvariantCulture);
        _pinBox.Text = pin;
        return true;
    }

    bool HasSavedEndpoint()
    {
        var prefs = Prefs();
        return RemoteNet.TryParseAddress(prefs.GetString(PrefHost, ""), prefs.GetString(PrefPort, ""), out _, out _, out _)
            && RemoteNet.IsSixDigitPin((prefs.GetString(PrefPin, "") ?? "").Trim());
    }

    void OnSave(object? sender, EventArgs e)
    {
        var pin = (_pinBox.Text ?? "").Trim();
        if (!RemoteNet.TryParseAddress(_hostBox.Text, _portBox.Text, out var host, out var port, out var error))
        {
            ShowSetup(error, allowBack: true);
            return;
        }

        if (!RemoteNet.IsSixDigitPin(pin))
        {
            ShowSetup("PIN is the 6 digits from the PC.", allowBack: true);
            return;
        }

        var editor = Prefs().Edit();
        if (editor is null)
            return;
        editor.PutString(PrefHost, host);
        editor.PutString(PrefPort, port.ToString(CultureInfo.InvariantCulture));
        editor.PutString(PrefPin, pin);
        editor.Commit();
        _hostBox.Text = host;
        _portBox.Text = port.ToString(CultureInfo.InvariantCulture);
        _pinBox.Text = pin;
        _client.Apply(host, port, pin);
        ShowRemote();
    }

    void ShowSetup(string? error, bool allowBack)
    {
        if (IsDestroyed)
            return;
        NextGeneration();
        CancelSession();
        _mode = ScreenMode.Setup;
        _remoteView.Visibility = ViewStates.Gone;
        _setupView.Visibility = ViewStates.Visible;
        _back.Visibility = allowBack && HasSavedEndpoint() ? ViewStates.Visible : ViewStates.Gone;
        if (string.IsNullOrWhiteSpace(error))
        {
            _setupError.Text = "";
            _setupError.Visibility = ViewStates.Gone;
        }
        else
        {
            _setupError.Text = error;
            _setupError.Visibility = ViewStates.Visible;
        }
    }

    void ShowRemote()
    {
        if (IsDestroyed)
            return;
        _mode = ScreenMode.Remote;
        _setupView.Visibility = ViewStates.Gone;
        _remoteView.Visibility = ViewStates.Visible;
        if (_resumed)
            StartSession();
    }

    void StartSession()
    {
        CancelSession();
        var generation = NextGeneration();
        _session = new CancellationTokenSource();
        _ = RunSessionAsync(generation, _session.Token);
    }

    void CancelSession()
    {
        if (_session is null)
            return;
        try { _session.Cancel(); } catch (ObjectDisposedException) { }
        _oldSessions.Add(_session);
        _session = null;
    }

    int NextGeneration() => ++_generation;

    bool IsCurrent(int generation) => generation == _generation && !IsDestroyed;

    async Task RunSessionAsync(int generation, CancellationToken ct)
    {
        _ = LoadSourcesAsync(generation, ct);
        _ = SampleVolumeAsync(generation, ct);
        while (IsCurrent(generation) && !ct.IsCancellationRequested)
        {
            await RefreshStatusAsync(generation, ct);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (System.OperationCanceledException)
            {
                break;
            }
        }
    }

    async Task RefreshStatusAsync(int generation, CancellationToken ct)
    {
        if (!IsCurrent(generation))
            return;
        var call = await _client.GetStatusAsync(ct);
        if (!IsCurrent(generation) || call.Kind == RemoteCallKind.Cancelled)
            return;
        if (call.Kind == RemoteCallKind.Unauthorized)
        {
            ShowSetup("PIN was rejected.", allowBack: false);
            return;
        }

        if (call.Kind == RemoteCallKind.Unreachable)
        {
            ShowBanner("Can't reach the PC", reachError: true);
            return;
        }

        if (call.Kind != RemoteCallKind.Ok || call.Value is null)
        {
            ShowBanner(call.Detail ?? "Bad response");
            return;
        }

        ApplyStatus(call.Value);
    }

    async Task SampleVolumeAsync(int generation, CancellationToken ct)
    {
        var call = await _client.SampleVolumeAsync(ct);
        if (!IsCurrent(generation) || call.Kind == RemoteCallKind.Cancelled)
            return;
        if (call.Kind == RemoteCallKind.Unauthorized)
        {
            ShowSetup("PIN was rejected.", allowBack: false);
            return;
        }

        if (call.Kind != RemoteCallKind.Ok || call.Value is null)
            return;
        ApplyVolume(call.Value.Volume);
        if (call.Value.Muted is bool muted)
            _mute.Text = muted ? "Unmute" : "Mute";
    }

    async Task LoadSourcesAsync(int generation, CancellationToken ct)
    {
        var call = await _client.GetSourcesAsync(ct);
        if (!IsCurrent(generation) || call.Kind == RemoteCallKind.Cancelled)
            return;
        if (call.Kind == RemoteCallKind.Unauthorized)
        {
            ShowSetup("PIN was rejected.", allowBack: false);
            return;
        }

        if (call.Kind != RemoteCallKind.Ok || call.Value is null)
            return;
        _sources = call.Value;
        RebuildSlots();
    }

    void ApplyStatus(RemoteStatusResponse status)
    {
        _status = status;
        if (status.Busy)
            ShowBanner("Busy — already re-syncing / shuffling…");
        else if (_reachError || (_banner.Text?.StartsWith("Busy", StringComparison.Ordinal) ?? false))
            ShowBanner("");

        var np = status.NowPlaying;
        _title.Text = string.IsNullOrWhiteSpace(np?.Title) ? "Nothing playing" : np!.Title;
        SetLine(_artist, np?.Artist);
        SetLine(_album, np?.Album);
        _meta.Text = Describe(status, np);
        _play.Text = string.Equals(np?.State, "Playing", StringComparison.Ordinal) ? "Pause" : "Play";
        if (status.Muted is bool muted)
            _mute.Text = muted ? "Unmute" : "Mute";
        _wakeBar.Visibility = status.WakeActive ? ViewStates.Visible : ViewStates.Gone;
        ApplyVolume(status.Volume);
        ApplyRooms(status.Groups, status.ActiveRoom);
        ApplyArt(np);
        UpdateBusyChrome();
    }

    void ApplyVolume(int? volume)
    {
        if (_draggingVolume || volume is not int percent)
            return;
        if (_volume.Progress != percent)
            _volume.Progress = percent;
        _volumeLabel.Text = "Volume " + percent.ToString(CultureInfo.InvariantCulture);
    }

    void ApplyRooms(IReadOnlyList<RemoteGroupDto>? groups, string? activeRoom)
    {
        groups ??= [];
        var key = string.Join("\n", groups.Select(g => g.Room + "\t" + g.Label));
        if (key != _roomKey)
        {
            _roomKey = key;
            _rooms.Clear();
            _rooms.AddRange(groups);
            var labels = _rooms
                .Select(g => string.IsNullOrWhiteSpace(g.Label) ? g.Room : g.Label)
                .ToList();
            if (labels.Count == 0)
                labels.Add("No rooms");
            _roomSuppress = true;
            _roomsSpinner.Adapter = new RoomAdapter(this, labels);
            _roomSuppress = false;
        }

        if (_postedRoom is not null && string.Equals(activeRoom, _postedRoom, StringComparison.OrdinalIgnoreCase))
            _postedRoom = null;

        var target = _postedRoom ?? activeRoom;
        var index = _rooms.FindIndex(g => string.Equals(g.Room, target, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = 0;
        if (_rooms.Count == 0)
        {
            _spinnerRoom = null;
            return;
        }

        var room = _rooms[Math.Clamp(index, 0, _rooms.Count - 1)].Room;
        if (string.Equals(room, _spinnerRoom, StringComparison.OrdinalIgnoreCase)
            && _roomsSpinner.SelectedItemPosition == index)
            return;

        _roomSuppress = true;
        _roomsSpinner.SetSelection(index);
        _spinnerRoom = room;
        _roomSuppress = false;
    }

    void ApplyArt(RemoteNowPlayingDto? np)
    {
        if (np is not { HasArt: true } || string.IsNullOrEmpty(np.ArtRevision))
        {
            if (_artRevision is not null || _artInFlight is not null || _art.Visibility == ViewStates.Visible)
                ClearArt();
            return;
        }

        if (np.ArtRevision == _artRevision || np.ArtRevision == _artInFlight)
            return;

        var revision = np.ArtRevision;
        var generation = _generation;
        _artInFlight = revision;
        var token = _session?.Token ?? _alive.Token;
        _ = FetchArtAsync(revision, generation, token);
    }

    async Task FetchArtAsync(string revision, int generation, CancellationToken ct)
    {
        var call = await _client.GetArtAsync(ct);
        if (!IsCurrent(generation))
        {
            if (_artInFlight == revision)
                _artInFlight = null;
            return;
        }

        if (call.Kind is RemoteCallKind.Cancelled or RemoteCallKind.Unreachable)
        {
            if (_artInFlight == revision)
                _artInFlight = null;
            return;
        }

        if (call.Kind == RemoteCallKind.Unauthorized)
        {
            ShowSetup("PIN was rejected.", allowBack: false);
            return;
        }

        if (call.Value is not { Length: > 0 })
        {
            _artRevision = revision;
            _artInFlight = null;
            _art.SetImageBitmap(null);
            _art.Visibility = ViewStates.Gone;
            return;
        }

        Bitmap? bmp;
        try
        {
            var bytes = call.Value;
            bmp = await Task.Run(() => BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length), ct);
        }
        catch (System.OperationCanceledException)
        {
            if (_artInFlight == revision)
                _artInFlight = null;
            return;
        }

        if (!IsCurrent(generation) || bmp is null)
        {
            bmp?.Dispose();
            if (_artInFlight == revision)
                _artInFlight = null;
            return;
        }

        _artRevision = revision;
        _artInFlight = null;

        var previous = _artBitmap;
        _artBitmap = bmp;
        _art.SetImageBitmap(bmp);
        _art.Visibility = ViewStates.Visible;
        if (!ReferenceEquals(previous, bmp))
            previous?.Dispose();
    }

    void ClearArt()
    {
        _artInFlight = null;
        _artRevision = null;
        _art.SetImageBitmap(null);
        _art.Visibility = ViewStates.Gone;
        _artBitmap?.Dispose();
        _artBitmap = null;
    }

    async Task SendFast(Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>> work)
    {
        var generation = _generation;
        try
        {
            var call = await work(_alive.Token);
            if (!IsCurrent(generation))
                return;
            ApplyCommand(call);
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation))
                ShowBanner(ex.Message);
        }
    }

    async Task RunSlow(Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>> work)
    {
        if (_slow > 0 || _status?.Busy == true)
        {
            ShowBanner("Busy — already re-syncing / shuffling…");
            return;
        }

        var generation = _generation;
        _slow++;
        UpdateBusyChrome();
        try
        {
            var call = await work(_alive.Token);
            if (!IsCurrent(generation))
                return;
            ApplyCommand(call);
            if (call.Kind == RemoteCallKind.Ok && call.Value is { Ok: true })
                await RefreshStatusAsync(generation, _alive.Token);
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation))
                ShowBanner(ex.Message);
        }
        finally
        {
            _slow = Math.Max(0, _slow - 1);
            UpdateBusyChrome();
        }
    }

    void ApplyCommand(RemoteCall<RemoteCommandResponse> call)
    {
        if (IsDestroyed || call.Kind == RemoteCallKind.Cancelled)
            return;
        if (call.Kind == RemoteCallKind.Unauthorized)
        {
            ShowSetup("PIN was rejected.", allowBack: false);
            return;
        }

        if (call.Kind == RemoteCallKind.Unreachable)
        {
            ShowBanner("Can't reach the PC", reachError: true);
            return;
        }

        if (call.Value is { Ok: true })
        {
            var message = call.Value.Message;
            ShowBanner(string.IsNullOrWhiteSpace(message) ? "" : message);
            return;
        }

        ShowBanner(call.Value?.Error ?? call.Detail ?? "Request failed");
    }

    void ShowBanner(string text, bool reachError = false)
    {
        if (IsDestroyed)
            return;
        _reachError = reachError;
        if (string.IsNullOrWhiteSpace(text))
        {
            _banner.Text = "";
            _banner.Visibility = ViewStates.Gone;
            return;
        }

        _banner.Text = text;
        _banner.Visibility = ViewStates.Visible;
    }

    void UpdateBusyChrome()
    {
        if (IsDestroyed)
            return;
        var busy = _slow > 0 || _status?.Busy == true;
        _daily.Enabled = !busy;
        _daily.Text = busy ? "Busy…" : "Daily";
        for (var i = 1; i < _slotHost.ChildCount; i++)
        {
            if (_slotHost.GetChildAt(i) is Button button)
                button.Enabled = !busy;
        }
    }

    void RebuildSlots()
    {
        while (_slotHost.ChildCount > 1)
            _slotHost.RemoveViewAt(1);
        if (_sources?.Slots is null)
            return;

        foreach (var slot in _sources.Slots)
        {
            if (string.Equals(slot.Kind, "empty", StringComparison.OrdinalIgnoreCase))
                continue;
            var captured = slot;
            var label = string.IsNullOrWhiteSpace(slot.Label) ? "Slot " + slot.Index.ToString(CultureInfo.InvariantCulture) : slot.Label;
            var button = MakeButton(label, primary: false);
            button.Click += async (_, _) =>
            {
                try { await RunSlow(ct => _client.PlaySlotAsync(captured.Index, ct)); }
                catch (Exception ex) { ShowBanner(ex.Message); }
            };
            _slotHost.AddView(button, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(48))
            {
                LeftMargin = Dp(8),
            });
        }

        UpdateBusyChrome();
    }

    void OnRoomSelected(object? sender, AdapterView.ItemSelectedEventArgs e)
    {
        if (_roomSuppress || _rooms.Count == 0)
            return;
        if (e.Position < 0 || e.Position >= _rooms.Count)
            return;
        var room = _rooms[e.Position].Room;
        if (string.Equals(room, _spinnerRoom, StringComparison.OrdinalIgnoreCase))
            return;
        _spinnerRoom = room;
        _postedRoom = room;
        _ = SendRoomAsync(room);
    }

    async Task SendRoomAsync(string room)
    {
        var generation = _generation;
        try
        {
            var call = await _client.SetRoomAsync(room, _alive.Token);
            if (!IsCurrent(generation))
                return;
            ApplyCommand(call);
            if (call.Kind != RemoteCallKind.Ok || call.Value is not { Ok: true })
                _postedRoom = null;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(generation))
                return;
            _postedRoom = null;
            ShowBanner(ex.Message);
        }
    }

    void OpenSources()
    {
        if (_sources is null)
        {
            ShowBanner("Sources are not loaded yet.");
            if (_session is not null)
                _ = LoadSourcesAsync(_generation, _session.Token);
            return;
        }

        var col = Column();
        AddPlaySection(col, "Tags", _sources.Tags?.Select(tag => (tag.Label, (Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>>)(ct => _client.PlayTagAsync(tag.Key, ct)))).ToList());
        AddPlaySection(col, "Genres", _sources.Genres?.Select(genre => (genre.Name + " (" + genre.Count.ToString(CultureInfo.InvariantCulture) + ")", (Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>>)(ct => _client.PlayGenreAsync(genre.Name, ct)))).ToList());
        AddPlaySection(col, "Folders", _sources.Folders?.Select(folder =>
        {
            var label = folder.Name + (folder.Tracks > 0 ? " (" + folder.Tracks.ToString(CultureInfo.InvariantCulture) + ")" : "") + (folder.InDaily ? " · daily" : "");
            return (label, (Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>>)(ct => _client.PlayFolderAsync(folder.Path, ct)));
        }).ToList());
        ShowListDialog("Play from", col);
    }

    void AddPlaySection(LinearLayout col, string title, List<(string Label, Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>> Play)>? rows)
    {
        AddFull(col, Text(title, 13, "#8E8E8E"), 16);
        if (rows is null || rows.Count == 0)
        {
            AddFull(col, Text("None", 15, "#A8A8A8"), 4);
            return;
        }

        foreach (var row in rows)
        {
            var play = row.Play;
            var button = MakeButton(string.IsNullOrWhiteSpace(row.Label) ? title : row.Label, primary: false);
            button.Click += async (_, _) =>
            {
                DismissDialog();
                try { await RunSlow(play); }
                catch (Exception ex) { ShowBanner(ex.Message); }
            };
            AddFull(col, button, 8);
        }
    }

    void OpenSearch()
    {
        var query = Field("Artist, album, or title", InputTypes.ClassText | InputTypes.TextFlagNoSuggestions);
        query.ImeOptions = ImeAction.Search;
        var status = Text("", 14, "#F0D8A8");
        var results = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var more = MakeButton("More", primary: false);
        more.Visibility = ViewStates.Gone;
        var offset = 0;

        async Task RunAsync(bool append)
        {
            var text = query.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                status.Text = "Type something to search.";
                return;
            }

            if (!append)
                offset = 0;
            status.Text = "Searching…";
            var call = await _client.SearchAsync(text, 25, offset, _alive.Token);
            if (IsDestroyed)
                return;
            if (call.Kind == RemoteCallKind.Unauthorized)
            {
                DismissDialog();
                ShowSetup("PIN was rejected.", allowBack: false);
                return;
            }

            if (call.Kind == RemoteCallKind.Unreachable)
            {
                status.Text = "Can't reach the PC";
                return;
            }

            if (call.Kind != RemoteCallKind.Ok || call.Value is null)
            {
                status.Text = call.Detail ?? "Search failed";
                return;
            }

            if (!append)
                results.RemoveAllViews();
            if (call.Value.Tracks.Count == 0 && !append)
            {
                status.Text = "No matches";
                more.Visibility = ViewStates.Gone;
                return;
            }

            status.Text = "";
            foreach (var track in call.Value.Tracks)
                results.AddView(TrackRow(track));
            offset += call.Value.Tracks.Count;
            more.Visibility = call.Value.Tracks.Count == 25 ? ViewStates.Visible : ViewStates.Gone;
        }

        query.EditorAction += async (_, e) =>
        {
            if (e.ActionId != ImeAction.Search)
                return;
            e.Handled = true;
            try { await RunAsync(false); }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        var search = MakeButton("Search", primary: true);
        search.Click += async (_, _) =>
        {
            try { await RunAsync(false); }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        more.Click += async (_, _) =>
        {
            try { await RunAsync(true); }
            catch (Exception ex) { status.Text = ex.Message; }
        };

        var col = Column();
        AddFull(col, query, 0);
        AddFull(col, search, 8);
        AddFull(col, status, 8);
        AddFull(col, results, 4);
        AddFull(col, more, 8);
        ShowListDialog("Library", col);
        query.RequestFocus();
    }

    View TrackRow(RemoteTrackDto track)
    {
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.SetPadding(0, Dp(8), 0, Dp(4));
        var title = string.IsNullOrWhiteSpace(track.Title) ? "Untitled" : track.Title;
        box.AddView(Text(title, 16, "#FFFFFF", bold: true));
        var by = string.Join(" — ", new[] { track.Artist, track.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (by.Length > 0)
            box.AddView(Text(by, 13, "#A8A8A8"));
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var play = MakeButton("Play", primary: true);
        var queue = MakeButton("Queue", primary: false);
        play.Click += async (_, _) =>
        {
            DismissDialog();
            try { await RunSlow(ct => _client.PlayTrackAsync(track.Path, track.Title, track.Artist, ct)); }
            catch (Exception ex) { ShowBanner(ex.Message); }
        };
        queue.Click += async (_, _) =>
        {
            DismissDialog();
            try { await RunSlow(ct => _client.QueueTrackAsync(track.Path, track.Title, track.Artist, ct)); }
            catch (Exception ex) { ShowBanner(ex.Message); }
        };
        row.AddView(play, Weight());
        row.AddView(queue, Weight());
        box.AddView(row);
        return box;
    }

    void ShowListDialog(string title, View content)
    {
        var scroll = new ScrollView(this);
        var height = (int)((Resources?.DisplayMetrics?.HeightPixels ?? 800) * 0.65);
        scroll.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, height);
        scroll.AddView(content, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        var dialog = new AlertDialog.Builder(this)
            .SetTitle(title)
            ?.SetView(scroll)
            ?.SetNegativeButton("Close", (_, _) => { })
            ?.Create();
        if (dialog is null)
            return;
        ShowDialog(dialog);
    }

    void ShowDialog(AlertDialog dialog)
    {
        DismissDialog();
        _dialog = dialog;
        dialog.SetOnKeyListener(_dialogKeys);
        dialog.Show();
    }

    void DismissDialog()
    {
        try { _dialog?.Dismiss(); }
        catch (Exception) { /* the window may already be gone */ }
        _dialog = null;
    }

    void WireEvents()
    {
        _volume.StartTrackingTouch += (_, _) => _draggingVolume = true;
        _volume.ProgressChanged += (_, e) =>
        {
            if (e.FromUser)
                _volumeLabel.Text = "Volume " + e.Progress.ToString(CultureInfo.InvariantCulture);
        };
        _volume.StopTrackingTouch += (_, e) =>
        {
            _draggingVolume = false;
            var progress = e.SeekBar?.Progress ?? _volume.Progress;
            _volumeLabel.Text = "Volume " + progress.ToString(CultureInfo.InvariantCulture);
            _ = SendFast(ct => _client.SetVolumeAsync(progress, ct));
        };
        _roomsSpinner.ItemSelected += OnRoomSelected;
    }

    View BuildSetup()
    {
        var page = Column();
        page.SetPadding(Dp(20), Dp(28), Dp(20), Dp(24));
        AddFull(page, Text("HotSonos", 28, "#FFFFFF", bold: true), 0);
        AddFull(page, Text("Address and PIN from the PC tray menu.", 15, "#B5B5B5"), 8);
        _hostBox = Field("PC address", InputTypes.ClassText | InputTypes.TextFlagNoSuggestions);
        _portBox = Field("Port", InputTypes.ClassNumber);
        _portBox.Text = RemoteApi.DefaultPort.ToString(CultureInfo.InvariantCulture);
        _pinBox = Field("PIN", InputTypes.ClassNumber);
        _pinBox.SetFilters([new InputFilterLengthFilter(6)]);
        _hostBox.ImportantForAutofill = ImportantForAutofill.No;
        _pinBox.ImportantForAutofill = ImportantForAutofill.No;
        AddFull(page, _hostBox, 20);
        AddFull(page, _portBox, 12);
        AddFull(page, _pinBox, 12);
        _setupError = Text("", 14, "#F0B4B4");
        _setupError.Visibility = ViewStates.Gone;
        AddFull(page, _setupError, 12);
        var connect = MakeButton("Connect", primary: true);
        connect.Click += OnSave;
        _pinBox.ImeOptions = ImeAction.Done;
        _pinBox.EditorAction += (_, e) =>
        {
            if (e.ActionId != ImeAction.Done)
                return;
            e.Handled = true;
            OnSave(this, EventArgs.Empty);
        };
        AddFull(page, connect, 16);
        _back = MakeButton("Back", primary: false);
        _back.Visibility = ViewStates.Gone;
        _back.Click += (_, _) =>
        {
            if (TryApplySaved())
                ShowRemote();
        };
        AddFull(page, _back, 8);
        var scroll = new ScrollView(this);
        scroll.AddView(page);
        return scroll;
    }

    View BuildRemote()
    {
        var page = Column();
        page.SetPadding(Dp(16), Dp(16), Dp(16), Dp(24));

        _banner = Text("", 14, "#F0D8A8");
        _banner.Visibility = ViewStates.Gone;
        AddFull(page, _banner, 0);

        _wakeBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _wakeBar.SetBackgroundColor(Color.ParseColor("#3D2E14"));
        _wakeBar.SetPadding(Dp(12), Dp(8), Dp(8), Dp(8));
        _wakeBar.Visibility = ViewStates.Gone;
        var wakeText = Text("Wake is running", 15, "#F0D8A8");
        wakeText.Gravity = GravityFlags.CenterVertical;
        wakeText.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        var wakeCancel = MakeButton("Cancel", primary: false);
        wakeCancel.Click += async (_, _) =>
        {
            try { await SendFast(ct => _client.CancelWakeAsync(ct)); }
            catch (Exception ex) { ShowBanner(ex.Message); }
        };
        _wakeBar.AddView(wakeText);
        _wakeBar.AddView(wakeCancel);
        AddFull(page, _wakeBar, 8);

        _art = new ImageView(this);
        _art.SetScaleType(ImageView.ScaleType.FitCenter);
        _art.SetBackgroundColor(Color.Black);
        _art.Visibility = ViewStates.Gone;
        AddFull(page, _art, 12, Dp(220));

        _title = Text("HotSonos", 22, "#FFFFFF", bold: true);
        _artist = Text("", 16, "#E6E6E6");
        _album = Text("", 14, "#A8A8A8");
        _meta = Text("", 13, "#8E8E8E");
        AddFull(page, _title, 12);
        AddFull(page, _artist, 2);
        AddFull(page, _album, 2);
        AddFull(page, _meta, 4);

        var transport = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var previous = MakeButton("Previous", primary: false);
        _play = MakeButton("Play", primary: true);
        var next = MakeButton("Next", primary: false);
        previous.Click += async (_, _) => { try { await SendFast(ct => _client.PreviousAsync(ct)); } catch (Exception ex) { ShowBanner(ex.Message); } };
        _play.Click += async (_, _) => { try { await SendFast(ct => _client.PlayPauseAsync(ct)); } catch (Exception ex) { ShowBanner(ex.Message); } };
        next.Click += async (_, _) => { try { await SendFast(ct => _client.NextAsync(ct)); } catch (Exception ex) { ShowBanner(ex.Message); } };
        transport.AddView(previous, Weight());
        transport.AddView(_play, Weight());
        transport.AddView(next, Weight());
        AddFull(page, transport, 16);

        _volumeLabel = Text("Volume", 14, "#D0D0D0");
        AddFull(page, _volumeLabel, 16);
        _volume = new SeekBar(this) { Max = 100 };
        AddFull(page, _volume, 4);

        var levels = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _mute = MakeButton("Mute", primary: false);
        var level = MakeButton("Level", primary: false);
        _mute.Click += async (_, _) => { try { await SendFast(ct => _client.MuteAsync(ct)); } catch (Exception ex) { ShowBanner(ex.Message); } };
        level.Click += async (_, _) => { try { await SendFast(ct => _client.LevelAsync(ct)); } catch (Exception ex) { ShowBanner(ex.Message); } };
        levels.AddView(_mute, Weight());
        levels.AddView(level, Weight());
        AddFull(page, levels, 8);

        AddFull(page, Text("Room", 13, "#8E8E8E"), 16);
        _roomsSpinner = new Spinner(this);
        AddFull(page, _roomsSpinner, 4);

        AddFull(page, Text("Play", 13, "#8E8E8E"), 16);
        _slotHost = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _daily = MakeButton("Daily", primary: true);
        _daily.Click += async (_, _) =>
        {
            try { await RunSlow(ct => _client.PlayDailyAsync(ct)); }
            catch (Exception ex) { ShowBanner(ex.Message); }
        };
        _slotHost.AddView(_daily, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(48)));
        var scroller = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        scroller.AddView(_slotHost);
        AddFull(page, scroller, 4);

        var more = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var sources = MakeButton("Tags, genres, folders", primary: false);
        var search = MakeButton("Search", primary: false);
        sources.Click += (_, _) => OpenSources();
        search.Click += (_, _) => OpenSearch();
        more.AddView(sources, Weight());
        more.AddView(search, Weight());
        AddFull(page, more, 12);

        var change = MakeButton("Change PC", primary: false);
        change.Click += (_, _) => ShowSetup(null, allowBack: true);
        AddFull(page, change, 20);

        var scroll = new ScrollView(this);
        scroll.AddView(page);
        return scroll;
    }

    LinearLayout Column()
    {
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetBackgroundColor(Color.ParseColor("#121212"));
        return layout;
    }

    void AddFull(LinearLayout parent, View child, int topDp, int height = ViewGroup.LayoutParams.WrapContent)
    {
        parent.AddView(child, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, height)
        {
            TopMargin = Dp(topDp),
        });
    }

    LinearLayout.LayoutParams Weight()
    {
        return new LinearLayout.LayoutParams(0, Dp(48), 1f)
        {
            LeftMargin = Dp(4),
            RightMargin = Dp(4),
        };
    }

    TextView Text(string text, int sp, string color, bool bold = false)
    {
        var view = new TextView(this) { Text = text, TextSize = sp };
        view.SetTextColor(Color.ParseColor(color));
        if (bold)
            view.Typeface = Typeface.DefaultBold;
        return view;
    }

    EditText Field(string hint, InputTypes types)
    {
        var view = new EditText(this) { Hint = hint, InputType = types };
        view.SetSingleLine(true);
        view.SetTextColor(Color.White);
        view.SetHintTextColor(Color.ParseColor("#8A8A8A"));
        view.SetBackgroundColor(Color.ParseColor("#1E1E1E"));
        view.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        return view;
    }

    Button MakeButton(string text, bool primary)
    {
        var button = new Button(this) { Text = text };
        button.SetAllCaps(false);
        button.SetMinimumHeight(Dp(48));
        if (primary)
        {
            button.SetBackgroundColor(Color.ParseColor("#E39B2B"));
            button.SetTextColor(Color.ParseColor("#1A1206"));
        }

        return button;
    }

    int Dp(int value)
    {
        var metrics = Resources?.DisplayMetrics ?? throw new InvalidOperationException("Display metrics are not ready.");
        return (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, value, metrics);
    }

    ISharedPreferences Prefs() =>
        GetSharedPreferences(PrefName, FileCreationMode.Private)
        ?? throw new InvalidOperationException("Preferences are not available.");

    static void SetLine(TextView view, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            view.Text = "";
            view.Visibility = ViewStates.Gone;
            return;
        }

        view.Text = text;
        view.Visibility = ViewStates.Visible;
    }

    static string Describe(RemoteStatusResponse status, RemoteNowPlayingDto? np)
    {
        var bits = new List<string>();
        if (np is not null && !string.IsNullOrWhiteSpace(np.State))
            bits.Add(PrettyState(np.State));
        if (!string.IsNullOrWhiteSpace(np?.Source))
            bits.Add(np.Source);
        if (np?.Track is int track && np.Tracks is int count && count > 0)
            bits.Add(track.ToString(CultureInfo.InvariantCulture) + " / " + count.ToString(CultureInfo.InvariantCulture));
        if (status.Offline is { Count: > 0 })
            bits.Add("Offline: " + string.Join(", ", status.Offline));
        return string.Join("  ·  ", bits);
    }

    static string PrettyState(string state) => state switch
    {
        "Playing" => "Playing",
        "PausedPlayback" => "Paused",
        "Stopped" => "Stopped",
        "Transitioning" => "Changing",
        _ => state,
    };

    sealed class RoomAdapter : ArrayAdapter<string>
    {
        public RoomAdapter(Context context, IList<string> items)
            : base(context, Android.Resource.Layout.SimpleSpinnerItem, items)
        {
            SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        }

        public override View GetView(int position, View? convertView, ViewGroup parent)
        {
            var view = (TextView)base.GetView(position, convertView, parent)!;
            view.SetTextColor(Color.White);
            return view;
        }

        public override View GetDropDownView(int position, View? convertView, ViewGroup? parent)
        {
            var view = (TextView)base.GetDropDownView(position, convertView, parent)!;
            view.SetTextColor(Color.White);
            return view;
        }
    }

    sealed class DialogVolumeKeys : Java.Lang.Object, IDialogInterfaceOnKeyListener
    {
        readonly MainActivity _owner;

        public DialogVolumeKeys(MainActivity owner) => _owner = owner;

        public bool OnKey(IDialogInterface? dialog, Keycode keyCode, KeyEvent? e)
        {
            if (_owner._mode != ScreenMode.Remote)
                return false;
            if (keyCode is not (Keycode.VolumeUp or Keycode.VolumeDown))
                return false;
            if (e?.Action == KeyEventActions.Down)
            {
                var direction = keyCode == Keycode.VolumeUp ? 1 : -1;
                _ = _owner.SendFast(ct => _owner._client.StepVolumeAsync(direction, ct));
            }

            return true;
        }
    }
}

enum ScreenMode
{
    Setup,
    Remote,
}
