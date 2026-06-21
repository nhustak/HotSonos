namespace HotSonos.Core.Models;

/// <summary>Current AVTransport playback state of a group coordinator.</summary>
public enum SonosTransportState
{
    Unknown = 0,
    Stopped,
    Playing,
    PausedPlayback,
    Transitioning,
}

public static class SonosTransportStateParser
{
    /// <summary>Maps the raw CurrentTransportState string to the enum.</summary>
    public static SonosTransportState Parse(string? raw) => raw switch
    {
        "STOPPED" => SonosTransportState.Stopped,
        "PLAYING" => SonosTransportState.Playing,
        "PAUSED_PLAYBACK" => SonosTransportState.PausedPlayback,
        "TRANSITIONING" => SonosTransportState.Transitioning,
        _ => SonosTransportState.Unknown,
    };
}
