using HotSonos.Remote.Protocol;
using HotSonos.Remote.Views;

namespace HotSonos.Remote;

/// <summary>
/// Own tab bar. Android's bottom navigation stops at five and greys the rest out.
/// Player is the default. The strip scrolls so Queue, Reports, and Settings stay reachable.
/// </summary>
public partial class AppShell : ContentPage
{
    public const int SettingsIndex = 7;
    public const int MoreIndex = SettingsIndex;

    static AppShell? _current;

    readonly RemotePage[] _pages;
    readonly Button[] _tabs;
    readonly Stack<RemotePage> _stack = new();
    readonly Dictionary<string, RemotePage> _cached = new();
    int _index = -1;
    int _toast;

    public AppShell()
    {
        InitializeComponent();
#if IOS
        // .NET 10 pages are edge-to-edge unless this is set. Android keeps the default.
        SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.Container;
#endif
        _current = this;
        AppServices.Live.Start();
#if ANDROID
        MediaControls.Install();
        WearBridge.Install();
#endif
        _pages =
        [
            new PlayerPage(),
            new ControlPage(),
            new PlayPage(),
            new QueuePage(),
            new LibraryPage(),
            new TopologyPage(),
            new ReportsPage(),
            new SettingsPage(),
        ];
        var titles = new[] { "Player", "Now", "Play", "Queue", "Library", "Rooms", "Reports", "Settings" };
        _tabs = new Button[titles.Length];
        for (var i = 0; i < titles.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Text = titles[i],
                FontSize = 13,
                Padding = new Thickness(10, 8),
                BackgroundColor = Colors.Transparent,
                CornerRadius = 8,
                IsEnabled = true,
            };
            button.Clicked += (_, _) => Select(index);
            _tabs[i] = button;
            TabBar.Add(button);
        }

        Select(0);
#if ANDROID
        SizeChanged += (_, _) => LayoutTabs(Width);
#endif
    }

#if ANDROID
    /// <summary>Phone widths keep the scrolling strip. A wide window shows every tab at a capped width.</summary>
    void LayoutTabs(double width)
    {
        if (width <= 0 || _tabs.Length == 0)
            return;
        var each = RemoteWindow.TabButtonWidth(width, _tabs.Length);
        var spread = each > 0;
        TabBar.HorizontalOptions = spread ? LayoutOptions.Center : LayoutOptions.Start;
        TabHost.MinimumWidthRequest = Math.Max(0, TabScroll.Width);
        foreach (var tab in _tabs)
            tab.WidthRequest = spread ? each : -1;
    }
#endif

    public static void OpenConnection()
    {
        if (_current is null)
        {
            App.ShowConnect();
            return;
        }

        _current.PushCached("connection", () => new ConnectionSettingsPage());
    }

    public static void OpenSettings()
    {
        if (_current is not null)
            _current.Select(SettingsIndex);
        else
            App.ShowConnect();
    }

    internal static void Push(string key, Func<RemotePage> create) =>
        _current?.PushCached(key, create);

    public static void Pop() => _current?.PopPage();

    void PushCached(string key, Func<RemotePage> create)
    {
        if (!_cached.TryGetValue(key, out var page))
        {
            page = create();
            _cached[key] = page;
        }

        PushPage(page);
    }

    void PushPage(RemotePage page)
    {
        if (_stack.Count > 0 && ReferenceEquals(_stack.Peek(), page))
            return;

        if (_stack.Contains(page))
        {
            while (!ReferenceEquals(_stack.Peek(), page))
                _stack.Pop().NotifyHidden();
            Body.Content = page.TakeBody();
            page.NotifyShown();
            return;
        }

        if (_stack.Count == 0)
        {
            if (_index >= 0)
                _pages[_index].NotifyHidden();
        }
        else
            _stack.Peek().NotifyHidden();

        _stack.Push(page);
        Body.Content = page.TakeBody();
        page.NotifyShown();
    }

    void PopPage()
    {
        if (_stack.Count == 0 || _index < 0)
            return;
        _stack.Pop().NotifyHidden();
        var page = _stack.Count > 0 ? _stack.Peek() : _pages[_index];
        Body.Content = page.TakeBody();
        page.NotifyShown();
    }

    public void ShowToast(string text)
    {
        var mine = ++_toast;
        ToastText.Text = text;
        Toast.IsVisible = true;
        Toast.ZIndex = 10;
        _ = HideToastAsync(mine);
    }

    async Task HideToastAsync(int mine)
    {
        await Task.Delay(2200).ConfigureAwait(true);
        if (mine == _toast)
            Toast.IsVisible = false;
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _pages.Length)
            return;

        var overlay = _stack.Count > 0;
        while (_stack.Count > 0)
            _stack.Pop().NotifyHidden();

        if (index == _index && !overlay)
        {
            _pages[index].NotifyShown();
            RevealTab(index);
            return;
        }

        if (_index >= 0 && _index != index)
            _pages[_index].NotifyHidden();

        _index = index;
        var page = _pages[index];
        Body.Content = page.TakeBody();
        for (var i = 0; i < _tabs.Length; i++)
        {
            _tabs[i].IsEnabled = true;
            _tabs[i].TextColor = i == index ? Swatches.Accent : Swatches.Ink;
            _tabs[i].FontAttributes = i == index ? FontAttributes.Bold : FontAttributes.None;
        }

        page.NotifyShown();
        RevealTab(index);
    }

    void RevealTab(int index)
    {
        if (index < 0 || index >= _tabs.Length)
            return;
        _ = TabScroll.ScrollToAsync(_tabs[index], ScrollToPosition.MakeVisible, false);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
#if IOS
        if (width <= 0)
            return;
        // The tab strip sits in a grid so a wide Android window can center it.
        // Give that grid the viewport width or iPad centering has nothing to center in.
        TabHost.MinimumWidthRequest = Math.Max(0, width);
        // Eight labels fit on an iPad and sit in the middle. A phone keeps the
        // scrolling strip aligned to the start, same as Android.
        var measured = TabBar.Measure(double.PositiveInfinity, 80);
        var fits = measured.Width > 0 && measured.Width <= width - 8;
        var align = fits ? LayoutOptions.Center : LayoutOptions.Start;
        if (TabBar.HorizontalOptions != align)
            TabBar.HorizontalOptions = align;
#endif
    }
}
