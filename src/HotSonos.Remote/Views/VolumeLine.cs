using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

/// <summary>
/// 40dp mute control for a volume line. Open is a speaker with sound waves.
/// Muted is that speaker with a slash, on the danger color, so the state is obvious at a glance.
/// </summary>
sealed class MuteIcon
{
    public const double Size = 40;

    readonly GlyphView _glyph;

    public MuteIcon(Func<Task> click)
    {
        _glyph = new GlyphView { Kind = "speaker" };
        _glyph.WidthRequest = 24;
        _glyph.HeightRequest = 24;
        View = new Border
        {
            BackgroundColor = Swatches.Card,
            StrokeThickness = 1.5,
            Stroke = Swatches.Muted,
            WidthRequest = Size,
            HeightRequest = Size,
            MinimumWidthRequest = Size,
            MinimumHeightRequest = Size,
            Padding = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = Size / 2 },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = new Grid
            {
                WidthRequest = Size,
                HeightRequest = Size,
                Children = { _glyph },
            },
        };
        SetMuted(false);
        RemoteInput.MakeActivable(View, click);
    }

    public Border View { get; }

    public void SetMuted(bool muted)
    {
        _glyph.Kind = muted ? "muted" : "speaker";
        View.BackgroundColor = muted ? Swatches.Danger : Swatches.Card;
        View.Stroke = muted ? Swatches.Danger : Swatches.Muted;
        SemanticProperties.SetDescription(View, muted ? "Unmute" : "Mute");
    }
}

/// <summary>One Rooms row: the volume line, plus the label under the room name.</summary>
readonly record struct RoomVolumeLine(View View, Label Playing);

/// <summary>
/// One per-room volume line: name, what's playing, −, percent, +, mute icon. No slider.
/// − and + match the mute control's 40dp target so a phone row and a wide window stay compact.
/// A long-press on the name opens the room actions. The volume buttons stay on this line.
/// </summary>
static class VolumeLine
{
    public static RoomVolumeLine Room(
        string room,
        int volume,
        bool muted,
        bool reachable,
        int step,
        int generation,
        Func<int> generationNow,
        Func<int, Task<bool>> writeVolume,
        Func<bool, Task<bool>> writeMute,
        Func<Task>? hold = null)
    {
        var shown = Math.Clamp(volume, 0, 100);
        var applied = shown;
        var target = shown;
        var isMuted = muted;
        var sending = false;
        var holding = false;
        var token = 0;

        var name = new Label
        {
            Text = reachable ? room : room + " · offline",
            TextColor = Swatches.Ink,
            FontSize = 15,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        var playing = new Label
        {
            TextColor = Swatches.Muted,
            FontSize = 13,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            Margin = 0,
            Padding = 0,
            IsVisible = false,
            HorizontalOptions = LayoutOptions.Fill,
        };
        var names = new VerticalStackLayout
        {
            Spacing = 0,
            Padding = 0,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center,
            Children = { name, playing },
        };
        if (hold is not null)
        {
            SemanticProperties.SetDescription(names, room + ". Long press for room actions.");
            RemoteInput.MakeHoldable(names, hold);
            RemoteInput.MakeHoldable(name, hold);
            RemoteInput.MakeHoldable(playing, hold);
        }

        var percent = new Label
        {
            Text = shown + "%",
            TextColor = Swatches.Ink,
            FontSize = 16,
            FontAutoScalingEnabled = false,
            WidthRequest = 48,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.NoWrap,
        };
        SemanticProperties.SetDescription(percent, room + " " + shown + "%");

        async Task FlushAsync()
        {
            if (sending)
                return;
            sending = true;
            try
            {
                while (applied != target && generationNow() == generation)
                {
                    var next = target;
                    if (!await writeVolume(next).ConfigureAwait(true))
                    {
                        holding = false;
                        token++;
                        target = applied;
                        shown = applied;
                        percent.Text = shown + "%";
                        SemanticProperties.SetDescription(percent, room + " " + shown + "%");
                        break;
                    }

                    applied = next;
                }
            }
            finally
            {
                sending = false;
                if (applied != target && generationNow() == generation)
                    _ = FlushAsync();
            }
        }

        void Nudge(int direction)
        {
            if (generationNow() != generation)
                return;
            var next = PhoneVolume.Next(shown, direction, step);
            if (next == shown)
                return;
            shown = next;
            target = next;
            percent.Text = shown + "%";
            SemanticProperties.SetDescription(percent, room + " " + shown + "%");
            if (!sending)
                _ = FlushAsync();
        }

        async Task RepeatAsync(int direction, int mine)
        {
            await Task.Delay(420).ConfigureAwait(true);
            while (holding && token == mine && generationNow() == generation)
            {
                Nudge(direction);
                await Task.Delay(160).ConfigureAwait(true);
            }
        }

        void Begin(int direction)
        {
            var mine = ++token;
            holding = true;
            Nudge(direction);
            _ = RepeatAsync(direction, mine);
        }

        MuteIcon icon = null!;
        icon = new MuteIcon(async () =>
        {
            if (generationNow() != generation)
                return;
            var next = !isMuted;
            isMuted = next;
            icon.SetMuted(isMuted);
            var ok = await writeMute(next).ConfigureAwait(true);
            if (generationNow() != generation)
                return;
            if (!ok)
            {
                isMuted = !next;
                icon.SetMuted(isMuted);
            }
        });
        icon.SetMuted(isMuted);

        var row = new Grid
        {
            ColumnSpacing = 8,
            HorizontalOptions = LayoutOptions.Fill,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Put(row, names, 0);
        Put(row, Step("−", "Volume down", () => Begin(-1), () => holding = false), 1);
        Put(row, percent, 2);
        Put(row, Step("+", "Volume up", () => Begin(1), () => holding = false), 3);
        Put(row, icon.View, 4);
        return new RoomVolumeLine(RemoteUi.Card(row), playing);
    }

    static Button Step(string text, string description, Action pressed, Action released)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 22,
            FontAutoScalingEnabled = false,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            BackgroundColor = Swatches.Card,
            CornerRadius = 20,
            WidthRequest = MuteIcon.Size,
            HeightRequest = MuteIcon.Size,
            MinimumWidthRequest = MuteIcon.Size,
            MinimumHeightRequest = MuteIcon.Size,
            Padding = 0,
            Margin = 0,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        SemanticProperties.SetDescription(button, description);
        button.Pressed += (_, _) => pressed();
        button.Released += (_, _) => released();
        return button;
    }

    static void Put(Grid grid, View view, int column)
    {
        Grid.SetColumn(view, column);
        grid.Add(view);
    }
}
