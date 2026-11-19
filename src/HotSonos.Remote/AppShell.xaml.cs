using HotSonos.Remote.Views;

namespace HotSonos.Remote;

/// <summary>
/// Six tabs in our own bar. Android's bottom navigation stops at five and greys the rest out,
/// which hid Play, Library, Rooms, and More on a fresh install.
/// </summary>
public partial class AppShell : ContentPage
{
    public const int MoreIndex = 5;

    static AppShell? _current;

    readonly RemotePage[] _pages;
    readonly Button[] _tabs;
    int _index = -1;

    public AppShell()
    {
        InitializeComponent();
        _current = this;
        AppServices.Live.Start();
        _pages =
        [
            new ControlPage(),
            new PlayPage(),
            new LibraryPage(),
            new TopologyPage(),
            new ReportsPage(),
            new OptionsPage(),
        ];
        var titles = new[] { "Now", "Play", "Library", "Rooms", "Reports", "More" };
        _tabs = new Button[titles.Length];
        for (var i = 0; i < titles.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Text = titles[i],
                FontSize = 12,
                Padding = new Thickness(0, 8),
                BackgroundColor = Colors.Transparent,
                CornerRadius = 8,
                IsEnabled = true,
            };
            button.Clicked += (_, _) => Select(index);
            _tabs[i] = button;
            Grid.SetColumn(button, i);
            TabBar.Add(button);
        }

        Select(0);
    }

    public static void OpenConnection()
    {
        if (_current is not null)
            _current.Select(MoreIndex);
        else
            App.ShowConnect();
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _pages.Length)
            return;
        if (index == _index)
        {
            _pages[index].NotifyShown();
            return;
        }

        if (_index >= 0)
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
    }
}
