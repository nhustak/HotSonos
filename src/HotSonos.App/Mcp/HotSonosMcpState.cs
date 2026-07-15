using HotSonos.App.Models;
using HotSonos.App.Services;
using HotSonos.Core.Models;

namespace HotSonos.App.Mcp;

/// <summary>
/// Live app handles for MCP tools. Set from <see cref="App"/> after startup;
/// tools read this singleton via DI.
/// </summary>
public sealed class HotSonosMcpState
{
    public SonosManager Sonos { get; set; } = null!;
    public Func<AppSettings> Settings { get; set; } = () => AppSettings.CreateDefault();
    public WakeMusicService? Wake { get; set; }
    public Func<NowPlaying?> GetLastNowPlaying { get; set; } = () => null;
    public Func<Task<string>> RefreshDevicesAsync { get; set; } =
        () => Task.FromResult("Refresh not wired.");
    public string Endpoint { get; set; } = "";
    public bool IsRunning { get; set; }
}
