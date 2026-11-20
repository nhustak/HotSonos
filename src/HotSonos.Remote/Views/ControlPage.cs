using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

/// <summary>Favorite slots and Sonos favorites. Playback lives on the Player tab.</summary>
sealed class ControlPage : RemotePage
{
    readonly VerticalStackLayout _slots = new();
    readonly VerticalStackLayout _favorites = new();
    readonly Label _action = RemoteUi.Muted("");

    public ControlPage()
    {
        Content = RemoteUi.Page(
            "Now",
            RemoteUi.Section(PhoneSections.NowSlots, "Favorite slots", _slots),
            RemoteUi.Section(PhoneSections.NowFavorites, "Sonos favorites", _favorites),
            _action);
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadFavoritesAsync();
    }

    protected override void OnLinkChanged() => _ = LoadFavoritesAsync();

    async Task LoadFavoritesAsync()
    {
        if (!AppServices.Link.DrivePc)
        {
            await LoadSpeakerFavoritesAsync();
            return;
        }

        if (!AppServices.Link.OnPc)
        {
            _slots.Clear();
            _favorites.Clear();
            _slots.Add(RemoteUi.NeedsPc("Favorite slots"));
            _favorites.Add(RemoteUi.NeedsPc("Sonos favorites"));
            _action.Text = AppServices.Link.Problem ?? "Cannot reach the PC.";
            return;
        }

        var sources = await AppServices.Client.GetSourcesAsync(CancellationToken.None);
        _slots.Clear();
        if (sources.Value is { } src)
        {
            foreach (var slot in src.Slots)
            {
                var index = slot.Index;
                _slots.Add(RemoteUi.Button($"{slot.Index}. {slot.Label}", () => Act(ct => AppServices.Client.PlaySlotAsync(index, ct), _action)));
            }
        }

        var favorites = await AppServices.Client.GetFavoritesAsync(CancellationToken.None);
        _favorites.Clear();
        foreach (var fav in favorites.Value?.Favorites ?? [])
        {
            var name = fav.Name;
            _favorites.Add(RemoteUi.Button(name, () => Act(ct => AppServices.Client.PlayFavoriteAsync(name, ct), _action)));
        }
    }

    async Task LoadSpeakerFavoritesAsync()
    {
        _slots.Clear();
        _slots.Add(RemoteUi.NeedsPc("Favorite slots"));
        _favorites.Clear();

        var speaker = AppServices.Link.Speakers.Controller;
        if (speaker is null)
        {
            _action.Text = AppServices.Link.Problem ?? "No speakers yet.";
            return;
        }

        try
        {
            var favorites = await speaker.GetFavoritesAsync(CancellationToken.None);
            foreach (var fav in favorites.Where(f => f.IsPlayable))
            {
                var title = fav.Title;
                _favorites.Add(RemoteUi.Button(title, () => Speaker((s, ct) => s.PlayFavoriteByNameAsync(title, ct), _action)));
            }

            _action.Text = "Sonos favorites. HotSonos shuffle needs the PC.";
        }
        catch (Exception ex)
        {
            _action.Text = ex.Message;
        }
    }
}
