using System.Reflection;

namespace HotSonos.App.Infrastructure;

public static class AppVersion
{
    // Show the exact product version (the <Version> value, e.g. "1.0.0.2"),
    // taken from InformationalVersion so it matches the release/MSI exactly.
    public static string Current
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+'); // strip any +commit metadata, just in case
                return plus >= 0 ? info[..plus] : info;
            }
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        }
    }

    public static string DisplayName => $"HotSonos {Current}";
}
