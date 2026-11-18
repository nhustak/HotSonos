namespace HotSonos.Core.Models;

/// <summary>One track from the speaker's ContentDirectory index.</summary>
public sealed record SonosLibraryTrack(string Title, string Artist, string Album, string Uri);

/// <summary>One row in the coordinator's queue (Q:0).</summary>
public sealed record SonosQueueTrack(int Number, string Title, string Artist);

/// <summary>RenderingControl volume for one player.</summary>
public sealed record SonosSpeakerVolume(string Room, string Ip, int Volume, bool Muted, bool Reachable);

/// <summary>What the phone shows while it is talking to speakers directly.</summary>
public sealed record SonosHouseSnapshot(
    string? ActiveRoom,
    string? GroupLabel,
    IReadOnlyList<SonosZone> Zones,
    NowPlaying? Now,
    int? Volume,
    bool? Muted);
