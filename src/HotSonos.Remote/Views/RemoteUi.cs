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
            try { await click().ConfigureAwait(true); }
            catch (Exception ex)
            {
                if (Application.Current?.MainPage is Page page)
                    await page.DisplayAlert("HotSonos", ex.Message, "OK");
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

    public static ScrollView Page(string title, params View[] sections)
    {
        var stack = new VerticalStackLayout
        {
            Padding = new Thickness(16, 12, 16, 28),
            Spacing = 6,
            Children = { Heading(title) },
        };
        foreach (var section in sections)
            stack.Add(section);
        return new ScrollView { Content = stack, BackgroundColor = Swatches.Bg };
    }
}

abstract class RemotePage : ContentPage
{
    protected RemotePage()
    {
        BackgroundColor = Swatches.Bg;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AppServices.Live.Changed += OnLive;
        OnStatus(AppServices.Live.Status);
    }

    protected override void OnDisappearing()
    {
        AppServices.Live.Changed -= OnLive;
        base.OnDisappearing();
    }

    void OnLive() => OnStatus(AppServices.Live.Status);

    protected virtual void OnStatus(RemoteStatusResponse? status)
    {
    }

    protected Task<bool> Confirm(string title, string message) =>
        DisplayAlert(title, message, "Do it", "Cancel");

    protected async Task<bool> Act(Func<CancellationToken, Task<RemoteCall<RemoteCommandResponse>>> call, Label? status = null)
    {
        var result = await call(CancellationToken.None).ConfigureAwait(true);
        if (result.Kind == RemoteCallKind.Unauthorized)
        {
            await DisplayAlert("PIN", "The PC rejected the PIN. Open More and pair again.", "OK");
            return false;
        }

        if (result.Kind != RemoteCallKind.Ok || result.Value is not { Ok: true } cmd)
        {
            await DisplayAlert("HotSonos", result.Value?.Error ?? result.Detail ?? "Could not reach the PC.", "OK");
            return false;
        }

        if (status is not null)
            status.Text = string.IsNullOrWhiteSpace(cmd.Message) ? "Done" : cmd.Message;
        return true;
    }
}
