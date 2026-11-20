using HotSonos.Remote.Protocol;
using Microsoft.Maui.Storage;

namespace HotSonos.Remote;

/// <summary>Section open/closed state on the phone. Survives leaving a tab and restarting the app.</summary>
sealed class PreferencesSectionStore : IPhoneSectionStore
{
    const string Prefix = "hotsonos.section.";

    public bool? Read(string key)
    {
        var name = Prefix + key;
        return Preferences.ContainsKey(name) ? Preferences.Get(name, false) : null;
    }

    public void Write(string key, bool open) => Preferences.Set(Prefix + key, open);
}
