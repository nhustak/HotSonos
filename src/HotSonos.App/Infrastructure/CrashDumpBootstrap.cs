using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Win32;

namespace HotSonos.App.Infrastructure;

/// <summary>
/// Hard-death capture. Managed logs / Lifecycle / ProcessExit often never run on
/// StackOverflow / exitCode=-1. Empty crashes/ folders meant dumps were never
/// enabled effectively ΓÇö DOTNET_* alone has failed on this machine (exit -1, 0 .dmp).
/// We enable both runtime minidumps and Windows WER LocalDumps for HotSonos.exe.
/// </summary>
internal static class CrashDumpBootstrap
{
    public static string CrashDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotSonos", "crashes");

    public static string LastAlivePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotSonos", "last-alive.txt");

    /// <summary>Runs automatically as soon as the assembly loads ΓÇö before OnStartup.</summary>
    [ModuleInitializer]
    internal static void Enable()
    {
        try
        {
            Directory.CreateDirectory(CrashDirectory);

            // Absolute path; avoid relying only on %t which some hosts mishandle.
            // Runtime may still append uniqueness. Prefer one clear folder.
            var dumpPattern = Path.Combine(CrashDirectory, "hotsonos.%p.%t.dmp");

            Set("DOTNET_DbgEnableMiniDump", "1");
            Set("COMPlus_DbgEnableMiniDump", "1");
            Set("DOTNET_DbgMiniDumpType", "2"); // HeapWithThreadInfo
            Set("COMPlus_DbgMiniDumpType", "2");
            Set("DOTNET_DbgMiniDumpName", dumpPattern);
            Set("COMPlus_DbgMiniDumpName", dumpPattern);
            Set("DOTNET_CreateDumpDiagnostics", "1");
            Set("COMPlus_CreateDumpDiagnostics", "1");
            // Also write to default location if pattern fails
            Set("DOTNET_EnableCrashReport", "1");

            EnableWerLocalDumps();

            File.WriteAllText(
                Path.Combine(CrashDirectory, "dumps-enabled.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} dumps enabled{Environment.NewLine}" +
                $"dotnet pattern={dumpPattern}{Environment.NewLine}" +
                $"WER LocalDumps=HKCU\\...\\LocalDumps\\HotSonos.exe ΓåÆ {CrashDirectory}{Environment.NewLine}" +
                $"pid={Environment.ProcessId}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            /* never throw from module init */
        }
    }

    /// <summary>
    /// Windows Error Reporting local dumps ΓÇö catches more hard kills than DOTNET_* alone
    /// (we observed exitCode=-1 with DOTNET_DbgEnableMiniDump set and still 0 .dmp files).
    /// </summary>
    private static void EnableWerLocalDumps()
    {
        try
        {
            // Per-exe key under the current user (no admin required).
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\HotSonos.exe");
            if (key is null)
                return;

            key.SetValue("DumpFolder", CrashDirectory, RegistryValueKind.ExpandString);
            key.SetValue("DumpType", 2, RegistryValueKind.DWord); // Full dump
            key.SetValue("DumpCount", 15, RegistryValueKind.DWord);
        }
        catch
        {
            /* ignore ΓÇö still have DOTNET_* */
        }
    }

    /// <summary>
    /// Overwrite a tiny last-alive file (not the chatty daily log). Survives hard death
    /// as "last time we were still in managed code".
    /// </summary>
    private static readonly object AliveGate = new();

    public static void TouchAlive(string note)
    {
        try
        {
            var dir = Path.GetDirectoryName(LastAlivePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            lock (AliveGate)
            {
                File.WriteAllText(
                    LastAlivePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | pid={Environment.ProcessId} | {note}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            /* ignore */
        }
    }

    public static string Describe()
    {
        try
        {
            var n = Directory.Exists(CrashDirectory)
                ? Directory.GetFiles(CrashDirectory, "*.dmp").Length
                : 0;
            return $"Crash dumps: ON ΓåÆ {CrashDirectory} ({n} .dmp); " +
                   $"WER LocalDumps+DOTNET_Dbg; last-alive={LastAlivePath}";
        }
        catch (Exception ex)
        {
            return $"Crash dumps: status unknown ({ex.Message})";
        }
    }

    private static void Set(string name, string value) =>
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
}
