using HotSonos.Core;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

static class Swatches
{
    public static readonly Color Bg = Color.FromArgb("#12161C");
    public static readonly Color Card = Color.FromArgb("#1C2430");
    public static readonly Color Ink = Color.FromArgb("#F4F7FB");
    public static readonly Color Muted = Color.FromArgb("#9AA8B5");
    public static readonly Color Accent = Color.FromArgb("#2ECC71");
    public static readonly Color Danger = Color.FromArgb("#E35D6A");
}

static class RemoteUi
{
    public static Label Heading(string text) => new()
    {
        Text = text,
        TextColor = Swatches.Ink,
        FontSize = 22,
        FontAttributes = FontAttributes.Bold,
        Margin = new Thickness(0, 4, 0, 8),
    };

    public static Label Muted(string text) => new()
    {
        Text = text,
        TextColor = Swatches.Muted,
        FontSize = 13,
    };

    public static Button Button(string text, Func<Task> click, bool danger = false)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = danger ? Swatches.Danger : Swatches.Accent,
            TextColor = Colors.White,
            CornerRadius = 8,
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 4),
        };
        button.Clicked += async (_, _) =>
        {
            if (!button.IsEnabled)
                return;
            button.IsEnabled = false;
            var opacity = button.Opacity;
            button.Opacity = 0.55;
            try
            {
                await click().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Toasts.Show(string.IsNullOrWhiteSpace(ex.Message) ? "Something went wrong." : ex.Message);
            }
            finally
            {
                button.Opacity = opacity <= 0 ? 1 : opacity;
                button.IsEnabled = true;
            }
        };
        return button;
    }

    public static Entry Field(string placeholder, Keyboard? keyboard = null) => new()
    {
        Placeholder = placeholder,
        TextColor = Swatches.Ink,
        PlaceholderColor = Swatches.Muted,
        BackgroundColor = Swatches.Card,
        Keyboard = keyboard ?? Keyboard.Default,
        Margin = new Thickness(0, 4),
    };

    public static View Card(params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(12) };
        foreach (var child in children)
            stack.Add(child);
        return new Border
        {
            BackgroundColor = Swatches.Card,
            StrokeThickness = 0,
            Padding = 4,
            Margin = new Thickness(0, 0, 0, 10),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = stack,
        };
    }

    public static ScrollView Page(string title, params View[] sections) =>
        BuildPage(title, showChip: true, sections);

    public static ScrollView ConnectPage(string title, params View[] sections) =>
        BuildPage(title, showChip: false, sections);

    static ScrollView BuildPage(string title, bool showChip, View[] sections)
    {
        var stack = new VerticalStackLayout
        {
            Padding = new Thickness(16, 12, 16, 28),
            Spacing = 6,
        };
#if ANDROID
        // Chromebook and other wide windows center a column. A phone stays full bleed.
        stack.HorizontalOptions = LayoutOptions.Center;
#endif
        stack.Add(Heading(title));
        if (showChip)
            stack.Add(ModeChip());
        foreach (var section in sections)
            stack.Add(section);
        var scroll = new ScrollView { Content = stack, BackgroundColor = Swatches.Bg };
#if ANDROID
        scroll.SizeChanged += (_, _) => Fit(stack, scroll.Width, RemoteWindow.PageColumnMax);
#endif
        return scroll;
    }

    /// <summary>Phone width stays full bleed. A wider window centers the column.</summary>
    public static void Fit(View view, double available, double max)
    {
        var column = RemoteWindow.Column(available, max);
        if (column <= 0)
            return;
        if (double.IsNaN(view.WidthRequest) || Math.Abs(view.WidthRequest - column) > 0.5)
            view.WidthRequest = column;
    }

    /// <summary>Tappable line on every screen: PC, Speakers, Speakers: room, or PC unreachable. Opens Connection / PC.</summary>
    public static View ModeChip()
    {
        var chip = new Button
        {
            FontAttributes = FontAttributes.Bold,
            FontSize = 14,
            BackgroundColor = Swatches.Card,
            CornerRadius = 8,
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalOptions = LayoutOptions.Fill,
        };
        void Paint()
        {
            var link = AppServices.Link;
            chip.Text = link.Banner;
            chip.TextColor = link.OnPc ? Color.FromArgb("#7EB6FF")
                : link.Outcome == PhoneLinkOutcome.PcDown ? Swatches.Danger
                : Swatches.Accent;
        }

        Paint();
        AppServices.Link.Changed += Paint;
        chip.Clicked += (_, _) => AppShell.OpenConnection();
        return chip;
    }

    public static Button Back() =>
        Button("Back", () =>
        {
            AppShell.Pop();
            return Task.CompletedTask;
        });

    public static View ModePicker()
    {
        var picker = new Picker
        {
            Title = "Link",
            TextColor = Swatches.Ink,
            BackgroundColor = Swatches.Card,
            Items = { "Auto — PC, then speakers", "PC only", "Speakers only" },
        };
        picker.SelectedIndex = (int)AppServices.Link.Preference;
        picker.SelectedIndexChanged += async (_, _) =>
        {
            if (picker.SelectedIndex < 0)
                return;
            AppServices.Link.SetPreference((PhoneLinkPreference)picker.SelectedIndex);
            await AppServices.Link.ResolveAsync(CancellationToken.None);
        };
        return picker;
    }

    /// <summary>
    /// Header plus chevron. The body hides when the section is closed.
    /// The choice is saved on the phone and restored the next time the page is built.
    /// </summary>
    public static View Section(string key, string title, params View[] children)
    {
        var open = PhoneSections.Resolve(key, AppServices.Sections);
        var body = new VerticalStackLayout { Spacing = 6, IsVisible = open };
        foreach (var child in children)
            body.Add(child);

        var chevron = new Label
        {
            Text = open ? "▼" : "▶",
            TextColor = Swatches.Accent,
            FontSize = 14,
            WidthRequest = 22,
            VerticalOptions = LayoutOptions.Center,
        };
        var heading = new Label
        {
            Text = title,
            TextColor = Swatches.Ink,
            FontAttributes = FontAttributes.Bold,
            FontSize = 16,
            VerticalOptions = LayoutOptions.Center,
        };
        var header = new Grid
        {
            Padding = new Thickness(0, 10),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
        };
        header.Add(chevron);
        Grid.SetColumn(heading, 1);
        header.Add(heading);

        chevron.InputTransparent = true;
        heading.InputTransparent = true;
        RemoteInput.MakeActivable(header, () =>
        {
            var next = !body.IsVisible;
            body.IsVisible = next;
            chevron.Text = next ? "▼" : "▶";
            AppServices.Sections.Write(key, next);
            return Task.CompletedTask;
        });

        return new VerticalStackLayout
        {
            Spacing = 2,
            Children = { header, body },
        };
    }

    public static View NeedsPc(string what)
    {
        var stack = new VerticalStackLayout { Spacing = 4 };
        stack.Add(Muted(RemoteFailureText.PcRequired(what)));
        stack.Add(Button("Open connection", () =>
        {
            AppShell.OpenConnection();
            return Task.CompletedTask;
        }));
        return stack;
    }
}

abstract class RemotePage : ContentPage
{
    protected RemotePage()
    {
        BackgroundColor = Swatches.Bg;
    }

    bool _listening;
    View? _held;

    public void NotifyShown()
    {
        if (!_listening)
        {
            _listening = true;
            AppServices.Live.Changed += OnLive;
            AppServices.Link.Changed += OnLink;
            OnStatus(AppServices.Live.Status);
        }

        OnShown();
    }

    public void NotifyHidden()
    {
        if (!_listening)
            return;
        _listening = false;
        AppServices.Live.Changed -= OnLive;
        AppServices.Link.Changed -= OnLink;
        OnHidden();
    }

    protected virtual void OnHidden()
    {
    }

    /// <summary>Pull the page body out so the tab host can show it. Android's shell bar only keeps five tabs.</summary>
    public View TakeBody()
    {
        _held ??= Content ?? throw new InvalidOperationException("Page has no content.");
        if (Content is not null)
            Content = null;
        if (_held.Parent is ContentView host)
            host.Content = null;
        return _held;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        NotifyShown();
    }

    protected override void OnDisappearing()
    {
        NotifyHidden();
        base.OnDisappearing();
    }

    protected virtual void OnShown()
    {
    }

    void OnLive() => OnStatus(AppServices.Live.Status);

    void OnLink() => MainThread.BeginInvokeOnMainThread(() => OnLinkChanged());

    protected virtual void OnLinkChanged()
    {
    }

    protected virtual void OnStatus(RemoteStatusResponse? status)
    {
    }

    protected SonosController RequireSpeaker() =>
        AppServices.Link.Speakers.Controller
        ?? throw new InvalidOperationException("No speakers yet. Pull to refresh, or check that the phone is on the same Wi-Fi.");

    protected async Task Speaker(Func<SonosController, CancellationToken, Task> work, Label? status = null, bool announce = false, string? done = null)
    {
        try
        {
            await work(RequireSpeaker(), CancellationToken.None).ConfigureAwait(true);
            var message = done ?? "Done";
            if (status is not null)
                status.Text = message;
            if (announce)
                Toasts.Show(message);
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message) ? "Could not reach the speakers." : ex.Message;
            if (status is not null)
                status.Text = message;
            Toasts.Show(message);
        }
    }

    protected Task<bool> Confirm(string title, string message) =>
        Dialogs.Confirm(this, title, message);

    protected async Task<bool> Act(
        Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>> call,
        Label? status = null,
        bool announce = false,
        string? done = null)
    {
        var result = await call(CancellationToken.None).ConfigureAwait(true);
        if (result.Kind == RemoteCallKind.Unauthorized)
        {
            var pin = result.Detail ?? "Wrong PIN. The PC rejected it. Open connection and enter the PIN from the PC Phone window.";
            if (status is not null)
                status.Text = pin;
            Toasts.Show(pin);
            await Dialogs.Alert(this, "PIN", pin);
            return false;
        }

        if (result.Kind != RemoteCallKind.Ok || result.Value is not { Ok: true } cmd)
        {
            var error = result.Value?.Error ?? result.Detail ?? "Could not reach the PC.";
            if (status is not null)
                status.Text = error;
            Toasts.Show(error);
            return false;
        }

        var detail = string.IsNullOrWhiteSpace(cmd.Message) ? (done ?? "Done") : cmd.Message;
        if (status is not null)
            status.Text = detail;
        if (announce)
            Toasts.Show(done ?? detail);
        return true;
    }
}
