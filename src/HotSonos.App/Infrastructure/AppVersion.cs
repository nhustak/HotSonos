using System.Reflection;

namespace HotSonos.App.Infrastructure;

public static class AppVersion
{
    public static string Current => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

    public static string DisplayName => $"HotSonos {Current}";
}
