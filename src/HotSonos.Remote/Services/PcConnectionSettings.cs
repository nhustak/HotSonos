using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>PC address and house PIN, saved in MAUI preferences.</summary>
sealed class PcConnectionSettings
{
    const string HostKey = "pc.host";
    const string PortKey = "pc.port";
    const string PinKey = "pc.pin";

    public string Host { get; set; } = "";
    public int Port { get; set; } = RemoteApi.DefaultPort;
    public string Pin { get; set; } = "";

    public bool IsPaired =>
        !string.IsNullOrWhiteSpace(Host) && RemoteNet.IsSixDigitPin(Pin) && Port is >= 1024 and <= 65535;

    public PcConnectionSettings() => Load();

    public void Load()
    {
        Host = Preferences.Get(HostKey, "");
        Port = Preferences.Get(PortKey, RemoteApi.DefaultPort);
        Pin = Preferences.Get(PinKey, "");
    }

    public void Save()
    {
        Host = Host.Trim();
        Pin = Pin.Trim();
        Preferences.Set(HostKey, Host);
        Preferences.Set(PortKey, Port);
        Preferences.Set(PinKey, Pin);
    }
}
