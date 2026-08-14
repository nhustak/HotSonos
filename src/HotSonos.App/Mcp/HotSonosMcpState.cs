using HotSonos.App.Library;
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
    public LibraryService? Library { get; set; }
    public Func<NowPlaying?> GetLastNowPlaying { get; set; } = () => null;
    public Func<Task<string>> RefreshDevicesAsync { get; set; } =
        () => Task.FromResult("Refresh not wired.");

    /// <summary>Runs a tray/hotkey action through the app gate (flyout + exclusive shuffle).</summary>
    public Func<HotsonosAction, Task<string?>> ExecuteActionAsync { get; set; } =
        _ => Task.FromResult<string?>("Action not wired.");

    /// <summary>Set active room by coordinator room name (same as tray room picker).</summary>
    public Action<string>? SetActiveRoom { get; set; }

    public string Endpoint { get; set; } = "";
    public bool IsRunning { get; set; }

    /// <summary>Persist settings after catalog edits from MCP (tag create/rename).</summary>
    public Action? PersistSettings { get; set; }

    /// <summary>Play one library track (UNC or x-file-cifs); replaces queue. Toast string.</summary>
    public Func<string, string?, string?, CancellationToken, Task<string>>? PlayLibraryTrackAsync { get; set; }

    /// <summary>Start a fresh history-aware library shuffle (return from one-shot).</summary>
    public Func<CancellationToken, Task<string>>? ResumeShuffleAsync { get; set; }

    /// <summary>Queue all tracks with a tag (label/key), optional shuffle; toast string.</summary>
    public Func<string, bool, CancellationToken, Task<string>>? PlayTaggedTracksAsync { get; set; }

    /// <summary>Queue all tracks with a standard Genre label, optional shuffle; toast string.</summary>
    public Func<string, bool, CancellationToken, Task<string>>? PlayGenreTracksAsync { get; set; }

    /// <summary>Shuffle one library folder (UNC path under Sonos roots).</summary>
    public Func<string, bool, CancellationToken, Task<string>>? PlayLibraryFolderAsync { get; set; }
}
