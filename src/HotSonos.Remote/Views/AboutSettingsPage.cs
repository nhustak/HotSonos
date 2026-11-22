using HotSonos.Remote.Protocol;
using Microsoft.Maui.ApplicationModel;

namespace HotSonos.Remote.Views;

sealed class AboutSettingsPage : RemotePage
{
    readonly Label _status = RemoteUi.Muted("");
    readonly VerticalStackLayout _heard = new();
    readonly VerticalStackLayout _logs = new();

    public AboutSettingsPage()
    {
        Content = RemoteUi.Page(
            "About",
            RemoteUi.Back(),
            new Label
            {
                Text = AboutCopy.Name,
                TextColor = Swatches.Ink,
                FontSize = 28,
                FontAttributes = FontAttributes.Bold,
                Margin = new Thickness(0, 4, 0, 0),
            },
            RemoteUi.Muted(VersionText()),
            RemoteUi.Muted(AboutCopy.AppId),
            RemoteUi.Muted(AboutCopy.Description),
            Link(AboutCopy.SiteLabel, AboutCopy.SiteUrl),
            Link(AboutCopy.GitHubLabel, AboutCopy.GitHubUrl),
            RemoteUi.Muted(AboutCopy.Trademark),
            _status,
            RemoteUi.Button("Failure diagnostic", DiagnosticAsync),
            RemoteUi.Button("Refresh", LoadAsync),
            RemoteUi.Section(PhoneSections.AboutHeard, "Heard outside the library", _heard),
            RemoteUi.Section(PhoneSections.AboutLog, "Recent PC log", _logs),
            RemoteUi.Muted("Hotkey capture, the Quick tag and Quick play overlays, the tray flyout, and Start with Windows stay on the PC. Player has a Quick tag button for the playing track."));
    }

    static string VersionText()
    {
        try
        {
            return AboutCopy.VersionLine(AppInfo.Current.VersionString);
        }
        catch (Exception)
        {
            return AboutCopy.VersionLine(null);
        }
    }

    static View Link(string label, string url)
    {
        var link = new Button
        {
            Text = label,
            TextColor = Swatches.Accent,
            BackgroundColor = Colors.Transparent,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Start,
            Padding = new Thickness(0, 2),
            Margin = 0,
        };
        SemanticProperties.SetDescription(link, url);
        link.Clicked += async (_, _) =>
        {
            try
            {
                await Launcher.Default.OpenAsync(url).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (Application.Current?.MainPage is Page page)
                    await page.DisplayAlert("HotSonos", ex.Message, "OK");
            }
        };
        return link;
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    async Task LoadAsync()
    {
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _heard.Clear();
            _logs.Clear();
            _heard.Add(RemoteUi.NeedsPc("Heard outside the library"));
            _logs.Add(RemoteUi.NeedsPc("Recent PC log"));
            _status.Text = AppServices.Link.DrivePc
                ? (AppServices.Link.Problem ?? "Cannot reach the PC.")
                : "Heard tracks and the PC log need the PC.";
            return;
        }

        var heard = await AppServices.Client.GetHeardAsync(CancellationToken.None);
        _heard.Clear();
        foreach (var track in heard.Value?.Tracks ?? [])
            _heard.Add(RemoteUi.Muted($"{track.Title} — {track.Artist} · {track.Plays}"));

        var logs = await AppServices.Client.GetLogsAsync(CancellationToken.None);
        _logs.Clear();
        foreach (var line in (logs.Value?.Lines ?? []).TakeLast(12))
            _logs.Add(RemoteUi.Muted(line));
        _status.Text = "";
    }

    async Task DiagnosticAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("The failure diagnostic");
            return;
        }

        if (!await Confirm("Failure diagnostic", "Run the PC diagnostic and save a report on the computer?"))
            return;
        await Act(ct => AppServices.Client.FailureDiagnosticAsync(ct), _status);
    }
}
