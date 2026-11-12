using System.IO;
using System.Text.Json;
using HotSonos.Core;

namespace HotSonos.App.Services;

/// <summary>
/// Spotify SMAPI session for listing playlists. Stored beside settings, not in git.
/// The file holds the link token. Do not log it.
/// </summary>
public sealed class SpotifySessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public SpotifySessionStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos", "spotify-smapi.json");
    }

    public string Path => _path;

    public SpotifySession? TryLoad()
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            var session = JsonSerializer.Deserialize<SpotifySession>(File.ReadAllText(_path), JsonOptions);
            return session is { IsComplete: true } ? session : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(SpotifySession session)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(session, JsonOptions));
    }
}
