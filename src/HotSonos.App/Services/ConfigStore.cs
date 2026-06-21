using System.IO;
using System.Text.Json;
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
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                    return settings.EnsureShape();
            }
        }
        catch
        {
            // Corrupt/unreadable config falls back to defaults rather than crashing the app.
        }

        return AppSettings.CreateDefault();
    }

    public void Save(AppSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings.EnsureShape(), JsonOptions);
        File.WriteAllText(_path, json);
    }
}
