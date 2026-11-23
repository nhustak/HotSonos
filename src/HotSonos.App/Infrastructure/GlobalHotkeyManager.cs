using System.Runtime.InteropServices;
using System.Windows.Interop;
using HotSonos.App.Models;

namespace HotSonos.App.Infrastructure;

/// <summary>
/// Registers system-wide hotkeys via Win32 RegisterHotKey and raises
/// <see cref="HotkeyPressed"/> when one fires. Backed by a message-only window
/// so it works even when no HotSonos window is visible.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private readonly Dictionary<int, HotsonosAction> _actionsById = [];
    private readonly Dictionary<int, string> _layoutsById = [];
    private int _nextId = 1;

    /// <summary>Raised on the UI thread when a registered hotkey is pressed.</summary>
    public event Action<HotsonosAction>? HotkeyPressed;

    /// <summary>Raised on the UI thread when a per-layout hotkey is pressed (layout id).</summary>
    public event Action<string>? LayoutHotkeyPressed;

    public GlobalHotkeyManager()
    {
        var parameters = new HwndSourceParameters("HotSonosHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            ParentWindow = HwndMessage, // message-only window
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Re-registers all hotkeys from <paramref name="settings"/>. Returns the
    /// actions whose hotkey could not be registered (unset or already in use).
    /// </summary>
    public IReadOnlyList<HotsonosAction> ApplyBindings(AppSettings settings)
    {
        UnregisterAll();

        var failures = new List<HotsonosAction>();
        foreach (var action in Enum.GetValues<HotsonosAction>())
        {
            var hotkey = settings.HotkeyFor(action);
            if (!hotkey.IsSet || hotkey.VirtualKey == 0)
                continue; // not configured — not a failure

            var id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, hotkey.Modifiers, hotkey.VirtualKey))
                _actionsById[id] = action;
            else
                failures.Add(action);
        }

        foreach (var (layoutId, hotkey) in settings.EnsureShape().LayoutHotkeys)
        {
            if (hotkey is null || !hotkey.IsSet || hotkey.VirtualKey == 0)
                continue;
            var id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, hotkey.Modifiers, hotkey.VirtualKey))
                _layoutsById[id] = layoutId;
        }

        return failures;
    }

    private void UnregisterAll()
    {
        foreach (var id in _actionsById.Keys)
            UnregisterHotKey(_source.Handle, id);
        foreach (var id in _layoutsById.Keys)
            UnregisterHotKey(_source.Handle, id);
        _actionsById.Clear();
        _layoutsById.Clear();
        _nextId = 1;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_actionsById.TryGetValue(id, out var action))
            {
                handled = true;
                HotkeyPressed?.Invoke(action);
            }
            else if (_layoutsById.TryGetValue(id, out var layoutId))
            {
                handled = true;
                LayoutHotkeyPressed?.Invoke(layoutId);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
