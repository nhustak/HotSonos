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
    readonly Label _tagStatus = RemoteUi.Muted("");
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
    readonly Grid _column;
#if ANDROID
    readonly Grid _host;
#endif
    readonly MuteIcon _mute;
    bool _muted;
    bool _muteBusy;
    DateTime _muteHoldUntil;
    bool _known;
    bool _sampling;
    bool _sampleAgain;
    bool _forceSample;
    string? _artRevision;
    RemoteStatusResponse? _status;

#if IOS
    /// <summary>iPad Player column. iPhone stays full width. Android does not compile this.</summary>
    const double IpadColumnWidth = 520;
#endif

    public PlayerPage()
    {
        _artFrame = new Border
        {
            BackgroundColor = Swatches.Card,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            WidthRequest = 280,
            HeightRequest = 280,
            MinimumWidthRequest = 280,
            MinimumHeightRequest = 280,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Start,
            Content = _art,
        };
        Tighten(_title);
        Tighten(_artist);
        Tighten(_album);

        _volume.FontSize = 16;
        _volume.TextColor = Swatches.Ink;
        _volume.LineBreakMode = LineBreakMode.TailTruncation;
        _volume.MaxLines = 1;
        _volume.HorizontalTextAlignment = TextAlignment.Center;
        _volume.VerticalTextAlignment = TextAlignment.Center;
        _volume.HorizontalOptions = LayoutOptions.Fill;
        _volume.VerticalOptions = LayoutOptions.Center;

        RemoteInput.OmitFromTabOrder(_slider);
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

        var previous = IconButton("prev", 78, "Previous", () => Transport(
            (speaker, ct) => speaker.PreviousAsync(ct),
            ct => AppServices.Client.PreviousAsync(ct)));
        _playButton = IconButton(_playGlyph, 108, "Play", () => Transport(
            async (speaker, ct) => { await speaker.PlayPauseAsync(ct); },
            ct => AppServices.Client.PlayPauseAsync(ct)));
        var next = IconButton("next", 78, "Next", () => Transport(
            (speaker, ct) => speaker.NextAsync(ct),
            ct => AppServices.Client.NextAsync(ct),
            previewNext: true));

        var transport = new HorizontalStackLayout
        {
            Spacing = 22,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { previous, _playButton, next },
        };

        _mute = new MuteIcon(ToggleMuteAsync);
        var stepRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Put(stepRow, StepButton("−", -1), 0);
        Put(stepRow, _volume, 1);
        Put(stepRow, StepButton("+", 1), 2);
        Put(stepRow, _mute.View, 3);

        var level = RemoteUi.Button("Level all", () => Transport(
            (_, ct) => AppServices.Link.Speakers.SetHouseVolumeAsync((int)_slider.Value, ct),
            ct => AppServices.Client.LevelAsync(ct),
            done: "Levels set"));
        var quickTag = RemoteUi.Button("Quick tag", QuickTagAsync);
        SemanticProperties.SetDescription(quickTag, "Quick tag");
        _tagStatus.HorizontalTextAlignment = TextAlignment.Center;

        var meta = new VerticalStackLayout
        {
            Spacing = 1,
            Margin = new Thickness(0, 4, 0, 0),
            Padding = 0,
            Children = { _title, _artist, _album },
        };
        // Art is a fixed square above the text. A star row used to center that square and
        // paint it over the title when the controls (slider, Quick tag) needed the rest of the screen.
        var block = new VerticalStackLayout
        {
            Spacing = 6,
            Padding = new Thickness(4, 0, 4, 4),
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Start,
            Children = { _artFrame, meta, transport, stepRow, _slider, level, quickTag, _tagStatus },
        };
        var scroll = new ScrollView
        {
            Content = block,
            BackgroundColor = Swatches.Bg,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
        };

        _column = new Grid
        {
            BackgroundColor = Swatches.Bg,
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Fill,
        };
        _column.Add(scroll);
#if IOS
        // iPad only: a centered column. The host stays full-bleed so the sides match the page.
        // iPhone leaves WidthRequest unset and fills the body.
        var host = new Grid
        {
            BackgroundColor = Swatches.Bg,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
        _column.HorizontalOptions = LayoutOptions.Fill;
        _column.VerticalOptions = LayoutOptions.Fill;
        host.Add(_column);
        _column.SizeChanged += (_, _) => FitPlayerColumn();
        Content = host;
#elif ANDROID
        // Chromebook and other wide windows: center a phone-width column. A phone fills the body.
        _host = new Grid { BackgroundColor = Swatches.Bg };
        _host.Add(_column);
        _host.SizeChanged += (_, _) =>
        {
            if (_host.Width > 0 && _host.Height > 0)
                Arrange(_host.Width, _host.Height);
        };
        Content = _host;
#else
        Content = _column;
#endif
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
#if IOS
        FitPlayerColumn();
#elif ANDROID
        // The shell reparents the body, so the page often has no size. Prefer the host that is on screen.
        if (_host.Width <= 0)
            Arrange(width, height);
#else
        if (width <= 0 || height <= 0)
            return;
        ApplyArt(RemoteWindow.ArtSide(width, height));
#endif
    }

#if ANDROID
    void Arrange(double width, double height)
    {
        if (width <= 0 || height <= 0)
            return;
        var column = RemoteWindow.Column(width, RemoteWindow.PlayerColumnMax);
        RemoteUi.Fit(_column, width, RemoteWindow.PlayerColumnMax);
        ApplyArt(RemoteWindow.ArtSide(column, height));
    }
#endif

#if IOS
    /// <summary>
    /// The page body is hosted in the shell, so the column grid is what actually gets a size.
    /// iPad caps that column. iPhone fills the width. Art sizing uses the column width.
    /// </summary>
    void FitPlayerColumn()
    {
        var height = _column.Height;
        if (height <= 0)
            return;

        var available = (_column.Parent as VisualElement)?.Width ?? _column.Width;
        if (available <= 0)
            return;

        var tablet = DeviceInfo.Idiom == DeviceIdiom.Tablet && available > IpadColumnWidth;
        var contentWidth = tablet ? IpadColumnWidth : available;
        var desiredRequest = tablet ? IpadColumnWidth : -1;
        if (Math.Abs(_column.WidthRequest - desiredRequest) > 0.5)
            _column.WidthRequest = desiredRequest;
        var align = tablet ? LayoutOptions.Center : LayoutOptions.Fill;
        if (_column.HorizontalOptions != align)
            _column.HorizontalOptions = align;

        ApplyArt(RemoteWindow.ArtSide(contentWidth, height));
    }
#endif

    void ApplyArt(double side)
    {
        if (side <= 0)
            return;
        _artFrame.WidthRequest = side;
        _artFrame.HeightRequest = side;
        _artFrame.MinimumWidthRequest = side;
        _artFrame.MinimumHeightRequest = side;
    }

    static void Tighten(Label label)
    {
        label.Margin = new Thickness(0);
        label.Padding = new Thickness(0);
        label.MaxLines = 1;
        label.LineHeight = 1.05;
        label.VerticalTextAlignment = TextAlignment.Start;
    }

    string? _skippedTitle;
    long _skippedAt;
    bool _previewedNext;

    async Task Transport(
        Func<SonosController, CancellationToken, Task> speakers,
        Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>> pc,
        bool previewNext = false,
        string? done = null)
    {
        if (previewNext)
            PreviewNext();
        if (AppServices.Link.DrivePc)
            await Act(pc, announce: true, done: done ?? "Done").ConfigureAwait(true);
        else
            await Speaker(speakers, announce: true, done: done ?? "Done").ConfigureAwait(true);
        await AppServices.Live.PullNowAsync().ConfigureAwait(true);
    }

    void PreviewNext()
    {
        if (_previewedNext)
            return;
        var face = SkipAhead.OnNext(AppServices.Live.Upcoming);
        if (face is null)
            return;
        _skippedTitle = _title.Text;
        _skippedAt = Environment.TickCount64;
        _previewedNext = true;
        _title.Text = face.Value.Title;
        _artist.Text = face.Value.Artist;
    }

    async Task QuickTagAsync()
    {
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _tagStatus.Text = "Quick tag needs the PC. It writes the playing library track.";
            Toasts.Show(_tagStatus.Text);
            return;
        }

        var listed = await AppServices.Client.GetNowTagsAsync(CancellationToken.None).ConfigureAwait(true);
        if (listed.Kind != RemoteCallKind.Ok || listed.Value is null)
        {
            _tagStatus.Text = listed.Detail ?? "Could not load tags.";
            Toasts.Show(_tagStatus.Text);
            return;
        }

        var value = listed.Value;
        if (!value.CanTag || string.IsNullOrWhiteSpace(value.Path))
        {
            _tagStatus.Text = value.Message ?? NowPlayingTagTarget.NotInLibrary;
            Toasts.Show(_tagStatus.Text);
            return;
        }

        if (value.Tags is not { Count: > 0 })
        {
            _tagStatus.Text = "No tags yet. Create them on the PC Tags tab.";
            Toasts.Show(_tagStatus.Text);
            return;
        }

        var labels = value.Tags.Select(tag => tag.On ? tag.Label + "  ✓" : tag.Label).ToArray();
        var pick = await Dialogs.Sheet(this, "Quick tag", "Cancel", null, labels).ConfigureAwait(true);
        var index = Array.IndexOf(labels, pick);
        if (index < 0)
            return;

        var toggled = await AppServices.Client.ToggleNowTagAsync(value.Tags[index].Key, CancellationToken.None).ConfigureAwait(true);
        _tagStatus.Text = toggled.Kind == RemoteCallKind.Ok
            ? (toggled.Value?.Message ?? "Updated")
            : (toggled.Value?.Error ?? toggled.Detail ?? "Tag failed");
        Toasts.Show(_tagStatus.Text);
    }

    protected override void OnStatus(RemoteStatusResponse? status)
    {
        _status = status;
        var np = status?.NowPlaying;
        var incoming = string.IsNullOrWhiteSpace(np?.Title) ? "Nothing playing" : np!.Title!;
        if (SkipAhead.IsStale(_skippedTitle, incoming, Environment.TickCount64 - _skippedAt))
        {
            // Next already painted the following queue row. This poll still names the track we left.
        }
        else
        {
            _skippedTitle = null;
            _previewedNext = false;
            _title.Text = incoming;
            _artist.Text = np?.Artist ?? "";
            _album.Text = np?.Album ?? "";
        }
        var playing = np?.State is "Playing" or "Transitioning";
        _playGlyph.Kind = playing ? "pause" : "play";
        SemanticProperties.SetDescription(_playButton, playing ? "Pause" : "Play");

        if (status?.Volume is int volume)
        {
            _known = true;
            if (DateTime.UtcNow >= _muteHoldUntil)
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
        _mute.SetMuted(_muted);
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
                if (DateTime.UtcNow >= _muteHoldUntil)
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

    async Task ToggleMuteAsync()
    {
        if (_muteBusy)
            return;
        _muteBusy = true;
        var previous = _muted;
        try
        {
            _muted = !previous;
            _muteHoldUntil = DateTime.UtcNow.AddSeconds(3);
            Paint(_shown);
            var ok = await CommitMuteAsync().ConfigureAwait(true);
            if (!ok)
            {
                _muted = previous;
                _muteHoldUntil = DateTime.MinValue;
                Paint(_shown);
                return;
            }

            Paint(_shown);
            _ = SampleAsync(force: true);
        }
        finally
        {
            _muteBusy = false;
        }
    }

    async Task<bool> CommitMuteAsync()
    {
        if (AppServices.Link.DrivePc)
            return await Act(ct => AppServices.Client.MuteAsync(ct)).ConfigureAwait(true);

        try
        {
            _muted = await AppServices.Link.Speakers.ToggleGroupMuteAsync(CancellationToken.None).ConfigureAwait(true);
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
        RemoteInput.MakeActivable(button, click);
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
                DrawSkip(canvas, w, h, TransportGlyph.Previous);
                break;
            case "next":
                DrawSkip(canvas, w, h, TransportGlyph.Next);
                break;
            case "speaker":
                DrawSpeaker(canvas, w, h, waves: true);
                break;
            case "muted":
                DrawSpeaker(canvas, w, h, waves: false);
                canvas.StrokeColor = Colors.White;
                canvas.StrokeSize = Math.Max(2.2f, w * 0.1f);
                canvas.StrokeLineCap = LineCap.Round;
                canvas.DrawLine(w * 0.56f, h * 0.22f, w * 0.92f, h * 0.78f);
                canvas.DrawLine(w * 0.92f, h * 0.22f, w * 0.56f, h * 0.78f);
                break;
            default:
                FillTriangle(canvas, w * 0.28f, h * 0.14f, w * 0.84f, h * 0.5f, w * 0.28f, h * 0.86f);
                break;
        }
    }

    static void DrawSpeaker(ICanvas canvas, float w, float h, bool waves)
    {
        canvas.FillColor = Colors.White;
        canvas.FillRoundedRectangle(w * 0.06f, h * 0.32f, w * 0.22f, h * 0.36f, w * 0.08f);
        var cone = new PathF();
        cone.MoveTo(w * 0.26f, h * 0.34f);
        cone.LineTo(w * 0.26f, h * 0.66f);
        cone.LineTo(w * 0.50f, h * 0.84f);
        cone.LineTo(w * 0.50f, h * 0.16f);
        cone.Close();
        canvas.FillPath(cone);
        if (!waves)
            return;
        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = Math.Max(2f, w * 0.09f);
        canvas.StrokeLineCap = LineCap.Round;
        StrokeWave(canvas, w * 0.58f, h, w * 0.12f);
        StrokeWave(canvas, w * 0.74f, h, w * 0.14f);
    }

    static void StrokeWave(ICanvas canvas, float x, float h, float reach)
    {
        var path = new PathF();
        path.MoveTo(x, h * 0.30f);
        path.QuadTo(x + reach, h * 0.50f, x, h * 0.70f);
        canvas.DrawPath(path);
    }

    static void DrawSkip(ICanvas canvas, float w, float h, TransportGlyph.Shape shape)
    {
        var triangle = shape.Triangle;
        FillTriangle(
            canvas,
            w * triangle[0].X, h * triangle[0].Y,
            w * triangle[1].X, h * triangle[1].Y,
            w * triangle[2].X, h * triangle[2].Y);
        var bar = shape.Bar;
        canvas.FillRoundedRectangle(w * bar.X, h * bar.Y, w * bar.Width, h * bar.Height, 2);
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
