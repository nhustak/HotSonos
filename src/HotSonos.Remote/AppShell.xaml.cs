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

    public AppShell()
    {
        InitializeComponent();
        _current = this;
        AppServices.Live.Start();
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
    }

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
}
