namespace HotSonos.Remote.Views;

static class SettingsForm
{
    public static View Labeled(string text, Switch toggle) => new HorizontalStackLayout
    {
        Spacing = 8,
        Children =
        {
            toggle,
            new Label { Text = text, TextColor = Swatches.Ink, VerticalOptions = LayoutOptions.Center },
        },
    };

    public static string Minutes(int minutes) =>
        $"{Math.Clamp(minutes, 0, 1439) / 60:00}:{Math.Clamp(minutes, 0, 1439) % 60:00}";

    public static bool TryMinutes(string? text, out int minutes)
    {
        minutes = 0;
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
            return false;
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;
        minutes = hour * 60 + minute;
        return true;
    }

    public static int Parse(string? text) => int.TryParse(text, out var n) ? n : 0;
}
