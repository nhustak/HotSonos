using System.Text.Json;
using HotSonos.Core;

namespace HotSonos.Remote;

/// <summary>
/// Layouts used when the phone talks to the speakers directly.
/// PC-linked mode uses the layouts saved on the PC instead.
/// </summary>
static class PhoneLayoutStore
{
    const string Key = "hotsonos.speaker-layouts";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static List<SpeakerLayout> Load()
    {
        var json = Preferences.Get(Key, "");
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<SpeakerLayout>>(json, Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<SpeakerLayout> layouts) =>
        Preferences.Set(Key, JsonSerializer.Serialize(layouts.ToList(), Options));
}
