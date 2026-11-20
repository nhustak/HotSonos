using System.IO;
using System.Text.Json;
using HotSonos.App.Infrastructure;
using HotSonos.App.Models;

namespace HotSonos.App.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON under %LocalAppData%\HotSonos.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public ConfigStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos", "settings.json");
    }

    public string Path => _path;

    /// <summary>Loads settings, or returns defaults if the file is missing/corrupt.</summary>
    public AppSettings Load()
    {
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                    loaded = settings.EnsureShape();
            }
        }
        catch (Exception ex)
        {
            // Corrupt/unreadable config falls back to defaults rather than crashing the app.
            AppLog.Warn($"Config load failed; using defaults ({_path})", ex);
        }

        loaded ??= AppSettings.CreateDefault();
        TryWritePhoneAdbFile(loaded);
        return loaded;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            settings = settings.EnsureShape();
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Config save failed ({_path})", ex);
            throw;
        }

        TryWritePhoneAdbFile(settings);
    }

    /// <summary>
    /// Sidecar for deploy scripts: <c>phone-adb.txt</c> next to settings.json, one line <c>host:port</c>.
    /// A failure here does not fail load or save of settings.json.
    /// </summary>
    private void TryWritePhoneAdbFile(AppSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
            return;
        if (!PhoneAdb.TryWriteAddressFile(directory, settings.PhoneAdbHost, settings.PhoneAdbPort, out var error))
            AppLog.Warn($"Phone adb address file was not written ({error})");
    }
}
