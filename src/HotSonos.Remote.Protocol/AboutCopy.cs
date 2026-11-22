namespace HotSonos.Remote.Protocol;

/// <summary>Identity block on Settings → About. Version text is filled from AppInfo at runtime.</summary>
public static class AboutCopy
{
    public const string Name = "HotSonos";

    public const string AppId = "com.tangenttech.hotsonos";

    public const string SiteUrl = "https://hotsonos.com";

    public const string SiteLabel = "hotsonos.com";

    public const string GitHubUrl = "https://github.com/nhustak/HotSonos";

    public const string GitHubLabel = "GitHub";

    public const string Description =
        "A remote for the HotSonos app on your PC, or standalone control of Sonos speakers on your Wi-Fi.";

    public const string Trademark =
        "HotSonos is an independent project and is not affiliated with or endorsed by Sonos, Inc. Sonos is a trademark of Sonos, Inc.";

    public static string VersionLine(string? version)
    {
        var text = version?.Trim();
        return string.IsNullOrEmpty(text) ? "Version unavailable" : "Version " + text;
    }
}
