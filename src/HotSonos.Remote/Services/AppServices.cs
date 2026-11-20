using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>Process-wide phone services. Screens pull on open and the live loop polls now-playing.</summary>
static class AppServices
{
    public static PcConnectionSettings Connection { get; } = new();
    public static RemoteClient Client { get; } = new();
    public static PhoneLink Link { get; } = new();
    public static RemoteLive Live { get; } = new();
    public static IPhoneSectionStore Sections { get; } = new PreferencesSectionStore();
}
