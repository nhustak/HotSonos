using HotSonos.Core;
using HotSonos.Remote.Protocol;
using Microsoft.Maui.Graphics;

namespace HotSonos.Remote.Views;

/// <summary>Default tab. Album art, transport, and volume only. Works on the PC and on the speakers.</summary>
sealed class PlayerPage : RemotePage
{
    readonly Image _art = new()
    {
        Aspect = Aspect.AspectFill,
        HorizontalOptions = LayoutOptions.Fill,
        VerticalOptions = LayoutOptions.Fill,
    };
    readonly Border _artFrame;
    readonly Label _title = new()
    {
        Text = "Nothing playing",
        TextColor = Swatches.Ink,
        FontSize = 24,
        FontAttributes = FontAttributes.Bold,
        HorizontalTextAlignment = TextAlignment.Center,
        LineBreakMode = LineBreakMode.TailTruncation,
    };
    readonly Label _artist = new()
    {
        TextColor = Swatches.Ink,
        FontSize = 17,
        HorizontalTextAlignment = TextAlignment.Center,
        LineBreakMode = LineBreakMode.TailTruncation,
    };
    readonly Label _album = new()
    {
        TextColor = Swatches.Muted,
        FontSize = 15,
        HorizontalTextAlignment = TextAlignment.Center,
        LineBreakMode = LineBreakMode.TailTruncation,
    };
    readonly Label _volume = RemoteUi.Muted(PhoneVolume.Label(null, false));
    readonly Slider _slider = new() { Minimum = 0, Maximum = 100, Value = 20 };
    readonly GlyphView _playGlyph = new() { Kind = "play" };
    readonly Border _playButton;

    bool _drag;
    bool _local;
    bool _sending;
    DateTime _acceptStatusAfter;
    bool _holding;
    int _repeat;
    int _shown = 20;
    int _target = 20;
    int _applied = 20;
    int _pcStep = PhoneVolume.SpeakerStep;
    bool _muted;
    bool _known;
    bool _sampling;
    bool _sampleAgain;
    bool _forceSample;
    string? _artRevision;
    RemoteStatusResponse? _status;

    public PlayerPage()
    {
        _artFrame = new Border
        {
            BackgroundColor = Swatches.Card,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            WidthRequest = 280,
            HeightRequest = 280,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = _art,
        };

        _volume.FontSize = 18;
        _volume.TextColor = Swatches.Ink;
        _volume.LineBreakMode = LineBreakMode.NoWrap;
        _volume.MaxLines = 1;
        _volume.HorizontalTextAlignment = TextAlignment.Center;
        _volume.VerticalTextAlignment = TextAlignment.Center;
        _volume.HorizontalOptions = LayoutOptions.Fill;
        _volume.VerticalOptions = LayoutOptions.Center;

        _slider.DragStarted += (_, _) => _drag = true;
        _slider.ValueChanged += (_, e) =>
        {
            if (!_drag)
                return;
            var percent = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
            _shown = percent;
            _volume.Text = PhoneVolume.Label(percent, _muted);
        };
        _slider.DragCompleted += (_, _) =>
        {
            _drag = false;
            var percent = Math.Clamp((int)_slider.Value, 0, 100);
            Pin();
            _target = percent;
            Paint(percent);
            if (!_sending)
                _ = FlushAsync();
        };

        var previous = IconButton("prev", 78, "Previous", () => Go(
            (speaker, ct) => speaker.PreviousAsync(ct),
            ct => AppServices.Client.PreviousAsync(ct)));
        _playButton = IconButton(_playGlyph, 108, "Play", () => Go(
            async (speaker, ct) => { await speaker.PlayPauseAsync(ct); },
            ct => AppServices.Client.PlayPauseAsync(ct)));
        var next = IconButton("next", 78, "Next", () => Go(
            (speaker, ct) => speaker.NextAsync(ct),
            ct => AppServices.Client.NextAsync(ct)));

        var transport = new HorizontalStackLayout
        {
            Spacing = 22,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { previous, _playButton, next },
        };

        var stepRow = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Put(stepRow, StepButton("−", -1), 0);
        Put(stepRow, _volume, 1);
        Put(stepRow, StepButton("+", 1), 2);

        var volumeRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() } };
        Put(volumeRow, RemoteUi.Button("Mute", () => Go(
            async (_, ct) => { await AppServices.Link.Speakers.ToggleGroupMuteAsync(ct); },
            ct => AppServices.Client.MuteAsync(ct))), 0);
        Put(volumeRow, RemoteUi.Button("Level all", () => Go(
            (_, ct) => AppServices.Link.Speakers.SetHouseVolumeAsync((int)_slider.Value, ct),
            ct => AppServices.Client.LevelAsync(ct))), 1);

        var controls = new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(8, 0, 8, 4),
            Children = { _title, _artist, _album, transport, stepRow, _slider, volumeRow },
        };

        var layout = new Grid
        {
            BackgroundColor = Swatches.Bg,
            Padding = new Thickness(16, 8, 16, 8),
            RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
        };
        layout.Add(_artFrame);
        Grid.SetRow(controls, 1);
        layout.Add(controls);
        Content = layout;
    }

    protected override void OnShown()
    {
        base.OnShown();
        PhoneVolumeKeys.Note = NoteHardware;
        PhoneVolumeKeys.Refresh = () => _ = SampleAsync(force: true);
        _ = LoadStepAsync();
        if (!_known)
            _ = SampleAsync();
    }

    protected override void OnLinkChanged()
    {
        _known = false;
        _ = LoadStepAsync();
        _ = SampleAsync(force: true);
    }

    protected override void OnHidden()
    {
        _holding = false;
        _repeat++;
        PhoneVolumeKeys.Note = null;
        PhoneVolumeKeys.Refresh = null;
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0)
            return;
        var side = Math.Min(width - 48, height * 0.46);
        side = Math.Clamp(side, 160, 420);
        _artFrame.WidthRequest = side;
        _artFrame.HeightRequest = side;
    }

    Task Go(Func<SonosController, CancellationToken, Task> speakers, Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>> pc) =>
        AppServices.Link.DrivePc ? Act(pc) : Speaker(speakers);

    protected override void OnStatus(RemoteStatusResponse? status)
    {
        _status = status;
        var np = status?.NowPlaying;
        _title.Text = string.IsNullOrWhiteSpace(np?.Title) ? "Nothing playing" : np!.Title;
        _artist.Text = np?.Artist ?? "";
        _album.Text = np?.Album ?? "";
        var playing = np?.State is "Playing" or "Transitioning";
        _playGlyph.Kind = playing ? "pause" : "play";
        SemanticProperties.SetDescription(_playButton, playing ? "Pause" : "Play");

        if (status?.Volume is int volume)
        {
            _known = true;
            _muted = status.Muted == true;
            var keepLocal = _local && volume != _shown && DateTime.UtcNow < _acceptStatusAfter;
            if (_drag || keepLocal)
                Paint(_shown);
            else
            {
                _local = false;
                _applied = volume;
                _target = volume;
                Paint(volume);
            }
        }
        else if (!_known && !_local && !_drag)
            _ = SampleAsync();

        if (np?.ArtRevision != _artRevision)
            _ = LoadArtAsync(np?.ArtRevision, np?.HasArt == true);
    }

    async Task LoadArtAsync(string? revision, bool hasArt)
    {
        _artRevision = revision;
        if (!AppServices.Link.DrivePc)
        {
            _art.Source = hasArt && Uri.TryCreate(revision, UriKind.Absolute, out var art) ? ImageSource.FromUri(art) : null;
            return;
        }

        if (!hasArt || string.IsNullOrEmpty(revision))
        {
            _art.Source = null;
            return;
        }

        var call = await AppServices.Client.GetArtAsync(CancellationToken.None);
        if (_artRevision != revision || call.Value is not { Length: > 0 } bytes)
            return;
        _art.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
    }

    Button StepButton(string text, int direction)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 34,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            BackgroundColor = Swatches.Card,
            CornerRadius = 36,
            WidthRequest = 72,
            HeightRequest = 72,
            Padding = 0,
            Margin = 0,
        };
        SemanticProperties.SetDescription(button, direction < 0 ? "Volume down" : "Volume up");
        button.Pressed += (_, _) => BeginRepeat(direction);
        button.Released += (_, _) => _holding = false;
        return button;
    }

    void BeginRepeat(int direction)
    {
        var token = ++_repeat;
        _holding = true;
        Nudge(direction);
        _ = RepeatAsync(direction, token);
    }

    async Task RepeatAsync(int direction, int token)
    {
        await Task.Delay(420).ConfigureAwait(true);
        while (_holding && _repeat == token)
        {
            Nudge(direction);
            await Task.Delay(160).ConfigureAwait(true);
        }
    }

    void Nudge(int direction)
    {
        var step = PhoneVolume.ConfiguredStep(AppServices.Link.DrivePc, _pcStep);
        var next = PhoneVolume.Next(_shown, direction, step);
        if (next == _shown)
            return;
        Pin();
        _target = next;
        Paint(next);
        if (!_sending)
            _ = FlushAsync();
    }

    void Pin()
    {
        _local = true;
        var until = DateTime.UtcNow.AddSeconds(3);
        if (until > _acceptStatusAfter)
            _acceptStatusAfter = until;
    }

    void NoteHardware(int direction)
    {
        if (!_known || _drag || _local)
            return;
        var step = PhoneVolume.ConfiguredStep(AppServices.Link.DrivePc, _pcStep);
        var next = PhoneVolume.Next(_shown, direction, step);
        _target = next;
        _applied = next;
        Paint(next);
    }

    void Paint(int volume)
    {
        _known = true;
        _shown = Math.Clamp(volume, 0, 100);
        _volume.Text = PhoneVolume.Label(_shown, _muted);
        if (!_drag)
            _slider.Value = _shown;
    }

    async Task SampleAsync(bool force = false)
    {
        if (force)
            _forceSample = true;
        if (_sampling)
        {
            _sampleAgain = true;
            return;
        }

        _sampling = true;
        try
        {
            do
            {
                _sampleAgain = false;
                var forceThis = _forceSample;
                _forceSample = false;
                var (volume, muted) = await ReadHouseVolumeAsync().ConfigureAwait(true);
                if (volume is not int n || _drag)
                    continue;
                if (_local && !forceThis)
                    continue;
                _muted = muted == true;
                _local = false;
                _applied = n;
                _target = n;
                Paint(n);
            }
            while (_sampleAgain);
        }
        catch (Exception)
        {
            // The next status poll tries again. The label stays on the last percent we showed.
        }
        finally
        {
            _sampling = false;
        }
    }

    async Task<(int? Volume, bool? Muted)> ReadHouseVolumeAsync()
    {
        if (AppServices.Link.DrivePc)
        {
            var call = await AppServices.Client.SampleVolumeAsync(CancellationToken.None).ConfigureAwait(true);
            return call.Value is { Ok: true } sample ? (sample.Volume, sample.Muted) : (null, null);
        }

        var snap = await AppServices.Link.Speakers.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
        return (snap.Volume, snap.Muted);
    }

    async Task LoadStepAsync()
    {
        if (!AppServices.Link.DrivePc)
            return;
        var call = await AppServices.Client.GetSettingsAsync(CancellationToken.None).ConfigureAwait(true);
        if (call.Value is { Ok: true } settings)
            _pcStep = PhoneVolume.Normalize(settings.VolumeStep);
    }

    async Task FlushAsync()
    {
        _sending = true;
        try
        {
            while (_applied != _target)
            {
                var percent = _target;
                var ok = AppServices.Link.DrivePc
                    ? await Act(ct => AppServices.Client.SetVolumeAsync(percent, ct)).ConfigureAwait(true)
                    : await SendSpeakerVolumeAsync(percent).ConfigureAwait(true);
                if (!ok)
                {
                    _holding = false;
                    _repeat++;
                    _local = false;
                    _target = _applied;
                    Paint(_applied);
                    break;
                }

                _applied = percent;
            }
        }
        finally
        {
            _sending = false;
        }
    }

    async Task<bool> SendSpeakerVolumeAsync(int percent)
    {
        try
        {
            await AppServices.Link.Speakers.SetHouseVolumeAsync(percent, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            if (Application.Current?.MainPage is Page page)
                await page.DisplayAlert("HotSonos", ex.Message, "OK").ConfigureAwait(true);
            return false;
        }
    }

    static Border IconButton(string kind, double size, string description, Func<Task> click) =>
        IconButton(new GlyphView { Kind = kind }, size, description, click);

    static Border IconButton(GlyphView glyph, double size, string description, Func<Task> click)
    {
        glyph.WidthRequest = size * 0.46;
        glyph.HeightRequest = size * 0.46;
        var button = new Border
        {
            BackgroundColor = size >= 100 ? Swatches.Accent : Swatches.Card,
            StrokeThickness = 0,
            WidthRequest = size,
            HeightRequest = size,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = size / 2 },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = new Grid { Children = { glyph } },
        };
        SemanticProperties.SetDescription(button, description);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try { await click().ConfigureAwait(true); }
            catch (Exception ex)
            {
                if (Application.Current?.MainPage is Page page)
                    await page.DisplayAlert("HotSonos", ex.Message, "OK");
            }
        };
        button.GestureRecognizers.Add(tap);
        return button;
    }

    static void Put(Grid grid, View view, int column)
    {
        Grid.SetColumn(view, column);
        grid.Add(view);
    }
}

sealed class GlyphView : GraphicsView
{
    string _kind;

    public GlyphView()
    {
        _kind = "play";
        Drawable = new GlyphDrawable(() => _kind);
        InputTransparent = true;
        HorizontalOptions = LayoutOptions.Center;
        VerticalOptions = LayoutOptions.Center;
    }

    public string Kind
    {
        get => _kind;
        set
        {
            if (_kind == value)
                return;
            _kind = value;
            Invalidate();
        }
    }
}

sealed class GlyphDrawable : IDrawable
{
    readonly Func<string> _kind;

    public GlyphDrawable(Func<string> kind) => _kind = kind;

    public void Draw(ICanvas canvas, RectF dirty)
    {
        canvas.FillColor = Colors.White;
        var w = dirty.Width;
        var h = dirty.Height;
        switch (_kind())
        {
            case "pause":
                var bar = w * 0.22f;
                var gap = w * 0.16f;
                var left = (w - bar * 2 - gap) / 2f;
                canvas.FillRoundedRectangle(left, h * 0.16f, bar, h * 0.68f, 3);
                canvas.FillRoundedRectangle(left + bar + gap, h * 0.16f, bar, h * 0.68f, 3);
                break;
            case "prev":
                canvas.FillRoundedRectangle(w * 0.12f, h * 0.18f, w * 0.14f, h * 0.64f, 2);
                FillTriangle(canvas, w * 0.88f, h * 0.16f, w * 0.34f, h * 0.5f, w * 0.88f, h * 0.84f);
                break;
            case "next":
                FillTriangle(canvas, w * 0.12f, h * 0.16f, w * 0.66f, h * 0.5f, w * 0.12f, h * 0.84f);
                canvas.FillRoundedRectangle(w * 0.74f, h * 0.18f, w * 0.14f, h * 0.64f, 2);
                break;
            default:
                FillTriangle(canvas, w * 0.28f, h * 0.14f, w * 0.84f, h * 0.5f, w * 0.28f, h * 0.86f);
                break;
        }
    }

    static void FillTriangle(ICanvas canvas, float x1, float y1, float x2, float y2, float x3, float y3)
    {
        var path = new PathF();
        path.MoveTo(x1, y1);
        path.LineTo(x2, y2);
        path.LineTo(x3, y3);
        path.Close();
        canvas.FillPath(path);
    }
}
