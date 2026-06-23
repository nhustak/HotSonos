using System.Reflection;

namespace HotSonos.App.Infrastructure;

public static class AppVersion
{
    // Show the clean 3-part version (e.g. "1.0.0") to match the release/README,
    // not the 4-part assembly version (1.0.0.0).
    public static string Current => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string DisplayName => $"HotSonos {Current}";
}
