using System.Drawing;
using System.Windows.Forms;

namespace HotSonos.App.Infrastructure;

/// <summary>
/// Owns the system-tray icon and its context menu. Transport items are static;
/// the room list and favorite slots are refreshed via Update* as topology and
/// settings change.
/// </summary>
public sealed class TrayController : IDisposable
{
    public sealed record Callbacks(
        Action OpenSettings,
        Action Refresh,
        Action FreshStart,
        Action ShuffleLibrary,
        Action PlayPause,
        Action Next,
        Action Previous,
        Action VolumeUp,
        Action VolumeDown,
        Action Mute,
        Action<int> PlayFavoriteSlot,
        Action LevelVolumes,
        Action<string> SetRoom,
        Action OpenLogFolder,
        Action CopyDiagnostics,
        Action Exit);

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _trayIcon;
    private readonly Callbacks _callbacks;
    private readonly string _versionLabel;

    private readonly ToolStripMenuItem _roomMenu;
    private readonly ToolStripMenuItem _favoritesMenu;
    private readonly ToolStripMenuItem _offlineItem;

    public TrayController(string versionLabel, Callbacks callbacks)
    {
        _callbacks = callbacks;
        _versionLabel = versionLabel;
        _menu = new ContextMenuStrip();

        _menu.Items.Add(new ToolStripMenuItem(versionLabel) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open HotSonos", null, (_, _) => _callbacks.OpenSettings());
        _menu.Items.Add("Refresh devices", null, (_, _) => _callbacks.Refresh());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("🔄 Restart fresh (re-sync all + shuffle)", null, (_, _) => _callbacks.FreshStart());
        _menu.Items.Add("🔀 Shuffle Music Library", null, (_, _) => _callbacks.ShuffleLibrary());
        _menu.Items.Add("Play / Pause", null, (_, _) => _callbacks.PlayPause());
        _menu.Items.Add("Next", null, (_, _) => _callbacks.Next());
        _menu.Items.Add("Previous", null, (_, _) => _callbacks.Previous());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Volume up", null, (_, _) => _callbacks.VolumeUp());
        _menu.Items.Add("Volume down", null, (_, _) => _callbacks.VolumeDown());
        _menu.Items.Add("Mute / Unmute", null, (_, _) => _callbacks.Mute());
        _menu.Items.Add("Level all speaker volumes", null, (_, _) => _callbacks.LevelVolumes());
        _menu.Items.Add(new ToolStripSeparator());

        _roomMenu = new ToolStripMenuItem("Room: (discovering…)");
        _favoritesMenu = new ToolStripMenuItem("Play favorite");
        _menu.Items.Add(_roomMenu);
        _menu.Items.Add(_favoritesMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _offlineItem = new ToolStripMenuItem("All speakers online") { Enabled = false };
        _menu.Items.Add(_offlineItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open log folder", null, (_, _) => _callbacks.OpenLogFolder());
        _menu.Items.Add("Copy diagnostics", null, (_, _) => _callbacks.CopyDiagnostics());
        _menu.Items.Add("Exit", null, (_, _) => _callbacks.Exit());

        _trayIcon = TrayIconFactory.Create();
        _notifyIcon = new NotifyIcon
        {
            Text = versionLabel,
            Icon = _trayIcon,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        // Double-click is the primary action: shuffle the library to all speakers.
        // Settings remain available via right-click → Open HotSonos.
        _notifyIcon.DoubleClick += (_, _) => _callbacks.ShuffleLibrary();
    }

    /// <summary>
    /// Rebuilds the group submenu. Each entry shows a group label and, when
    /// clicked, targets that group via its coordinator room name (the value).
    /// </summary>
    public void UpdateRooms(IReadOnlyList<(string Label, string Value)> groups, string? activeValue)
    {
        var activeLabel = groups.FirstOrDefault(g =>
            string.Equals(g.Value, activeValue, StringComparison.OrdinalIgnoreCase)).Label;
        _roomMenu.Text = string.IsNullOrEmpty(activeLabel) ? "Speakers: (none)" : $"Speakers: {activeLabel}";
        _roomMenu.DropDownItems.Clear();

        if (groups.Count == 0)
        {
            _roomMenu.DropDownItems.Add(new ToolStripMenuItem("(no speakers found)") { Enabled = false });
            return;
        }

        foreach (var (label, value) in groups)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = string.Equals(value, activeValue, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false,
            };
            var captured = value;
            item.Click += (_, _) => _callbacks.SetRoom(captured);
            _roomMenu.DropDownItems.Add(item);
        }
    }

    /// <summary>Rebuilds the favorite-slot submenu (4 entries).</summary>
    public void UpdateFavorites(IReadOnlyList<string?> slotNames)
    {
        _favoritesMenu.DropDownItems.Clear();
        for (var i = 0; i < slotNames.Count; i++)
        {
            var name = slotNames[i];
            var label = string.IsNullOrWhiteSpace(name) ? $"Slot {i + 1} (unset)" : $"{i + 1}. {name}";
            var item = new ToolStripMenuItem(label) { Enabled = !string.IsNullOrWhiteSpace(name) };
            var slotIndex = i;
            item.Click += (_, _) => _callbacks.PlayFavoriteSlot(slotIndex);
            _favoritesMenu.DropDownItems.Add(item);
        }
    }

    /// <summary>Updates the offline-speakers indicator line.</summary>
    public void UpdateOfflineSpeakers(IReadOnlyList<string> offline)
    {
        _offlineItem.Text = offline.Count == 0
            ? "All speakers online"
            : $"⚠ Offline: {string.Join(", ", offline)}";
    }

    /// <summary>Sets the tray hover tooltip to the current track (truncated to fit).</summary>
    public void UpdateNowPlaying(string? line)
    {
        var text = string.IsNullOrWhiteSpace(line) ? _versionLabel : $"♪ {line}";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..62] + "…";
    }

    public void ShowBalloon(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
        _menu.Dispose();
    }
}
