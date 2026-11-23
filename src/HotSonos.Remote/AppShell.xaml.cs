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
    TaskCompletionSource<string>? _menu;

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

    public Task<string> ShowMenu(string title, IReadOnlyList<string> actions)
    {
        _menu?.TrySetResult("");
        var tcs = new TaskCompletionSource<string>();
        _menu = tcs;
        BuildMenu(title ?? "", actions);
        MenuHost.IsVisible = true;
        MenuHost.ZIndex = 20;
        return tcs.Task;
    }

    void BuildMenu(string title, IReadOnlyList<string> actions)
    {
        var dim = new BoxView { Color = Color.FromArgb("#99000000") };
        RemoteInput.MakeActivable(dim, () =>
        {
            CloseMenu("");
            return Task.CompletedTask;
        });

        var handle = new BoxView
        {
            WidthRequest = 36,
            HeightRequest = 4,
            CornerRadius = 2,
            Color = Swatches.Muted,
            Opacity = 0.45,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 4, 0, 12),
        };
        var heading = new Label
        {
            Text = title,
            TextColor = Swatches.Ink,
            FontAttributes = FontAttributes.Bold,
            FontSize = 16,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            Margin = new Thickness(16, 0, 16, 8),
        };
        var items = new VerticalStackLayout { Spacing = 0 };
        foreach (var action in actions)
        {
            var label = action;
            var row = new Grid
            {
                HeightRequest = 48,
                Padding = new Thickness(16, 0),
            };
            row.Add(new Label
            {
                Text = label,
                TextColor = Swatches.Ink,
                FontSize = 16,
                VerticalTextAlignment = TextAlignment.Center,
            });
            RemoteInput.MakeActivable(row, () =>
            {
                CloseMenu(label);
                return Task.CompletedTask;
            });
            items.Add(row);
        }

        var cancel = new Label
        {
            Text = "Cancel",
            TextColor = Swatches.Muted,
            FontSize = 16,
            Padding = new Thickness(16, 14),
            HorizontalTextAlignment = TextAlignment.Center,
        };
        RemoteInput.MakeActivable(cancel, () =>
        {
            CloseMenu("");
            return Task.CompletedTask;
        });

        var card = new Border
        {
            BackgroundColor = Swatches.Card,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle
            {
                CornerRadius = new CornerRadius(16, 16, 0, 0),
            },
            Padding = new Thickness(0, 8, 0, 20),
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Fill,
            Content = new VerticalStackLayout
            {
                Spacing = 0,
                Children = { handle, heading, items, cancel },
            },
        };

        MenuHost.Children.Clear();
        MenuHost.Add(dim);
        MenuHost.Add(card);
    }

    void CloseMenu(string pick)
    {
        MenuHost.IsVisible = false;
        MenuHost.Children.Clear();
        var pending = _menu;
        _menu = null;
        pending?.TrySetResult(pick);
    }

    protected override bool OnBackButtonPressed()
    {
        if (MenuHost.IsVisible)
        {
            CloseMenu("");
            return true;
        }

        return base.OnBackButtonPressed();
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
