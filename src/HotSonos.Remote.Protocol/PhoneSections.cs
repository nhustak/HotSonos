namespace HotSonos.Remote.Protocol;

/// <summary>Remembers whether a phone section is open. Null means the phone has never toggled it.</summary>
public interface IPhoneSectionStore
{
    bool? Read(string key);
    void Write(string key, bool open);
}

/// <summary>In-memory store for tests and for a phone that has not been given preferences yet.</summary>
public sealed class MemorySectionStore : IPhoneSectionStore
{
    readonly Dictionary<string, bool> _values = new(StringComparer.Ordinal);

    public bool? Read(string key) => _values.TryGetValue(key, out var open) ? open : null;

    public void Write(string key, bool open) => _values[key] = open;
}

/// <summary>
/// Which stacked phone sections start open. Now playing and transport are not a section:
/// that screen is the Player tab and stays fully visible.
/// </summary>
public static class PhoneSections
{
    public const string NowSlots = "now.slots";
    public const string NowFavorites = "now.favorites";
    public const string PlayTags = "play.tags";
    public const string PlayGenres = "play.genres";
    public const string PlayFolders = "play.folders";
    public const string RoomsHouse = "rooms.house";
    public const string RoomsGroups = "rooms.groups";
    public const string RoomsSpeakers = "rooms.speakers";
    public const string RoomsVolume = "rooms.volume";
    public const string SpeakersRoom = "speakers.room";
    public const string SpeakersDaily = "speakers.daily";
    public const string LibraryFolders = "library.folders";
    public const string LibraryMaster = "library.master";
    public const string LibraryTags = "library.tags";
    public const string PlaybackShuffle = "playback.shuffle";
    public const string PlaybackOptions = "playback.options";
    public const string Wake = "wake.wake";
    public const string Nightly = "wake.nightly";
    public const string AboutHeard = "about.heard";
    public const string AboutLog = "about.log";
    public const string ReportFilters = "reports.filters";

    public static bool DefaultOpen(string key) => key switch
    {
        NowSlots or PlayTags or RoomsHouse or RoomsGroups or RoomsVolume or SpeakersDaily
            or LibraryFolders or PlaybackShuffle or Wake or ReportFilters => true,
        _ => false,
    };

    public static bool Resolve(string key, IPhoneSectionStore? store)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Section key is required.", nameof(key));
        return store?.Read(key.Trim()) ?? DefaultOpen(key.Trim());
    }
}
