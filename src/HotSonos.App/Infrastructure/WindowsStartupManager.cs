using Microsoft.Win32;

namespace HotSonos.App.Infrastructure;

/// <summary>Manages the HKCU Run-key entry that launches HotSonos at sign-in.</summary>
public static class WindowsStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HotSonos";
    public const string AutorunArgument = "--autorun";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var configuredValue = key?.GetValue(ValueName) as string;
        return string.Equals(configuredValue, BuildAutorunCommand(), StringComparison.Ordinal);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            key.SetValue(ValueName, BuildAutorunCommand());
            return;
        }

        if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }

    private static string BuildAutorunCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Unable to determine the current executable path for Windows startup.");

        return $"\"{processPath}\" {AutorunArgument}";
    }
}
