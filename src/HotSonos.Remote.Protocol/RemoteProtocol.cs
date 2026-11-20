using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace HotSonos.Remote.Protocol;

/// <summary>Shared constants for the phone remote and the tray host.</summary>
public static class RemoteApi
{
    public const string PinHeader = "X-HotSonos-Pin";
    public const int DefaultPort = 42342;

    public static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Which PC address the remote should dial, and which art URLs the PC may fetch.</summary>
public static class RemoteNet
{
    /// <summary>
    /// Picks one local IPv4 that shares a /24 with a known speaker.
    /// Returns null when no speaker is on a local subnet.
    /// </summary>
    public static string? ChooseLanAddress(IEnumerable<string>? localIpv4, IEnumerable<string>? speakerIpv4)
    {
        var speakers = Parse(speakerIpv4);
        if (speakers.Count == 0)
            return null;

        var locals = Parse(localIpv4)
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)
            .ToList();

        foreach (var local in locals)
        {
            var lb = local.GetAddressBytes();
            foreach (var speaker in speakers)
            {
                var sb = speaker.GetAddressBytes();
                if (lb[0] == sb[0] && lb[1] == sb[1] && lb[2] == sb[2])
                    return local.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// True only for http on port 1400 whose host is one of the supplied speaker addresses.
    /// The remote host uses this so album-art fetch cannot become an open proxy.
    /// </summary>
    public static bool IsAllowedArtUri(string? uri, IEnumerable<string>? speakerIps)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;
        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (!parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            return false;
        if (parsed.Port != 1400)
            return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo))
            return false;
        if (parsed.HostNameType != UriHostNameType.IPv4)
            return false;

        var allowed = new HashSet<string>(
            (speakerIps ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);
        return allowed.Contains(parsed.Host);
    }

    /// <summary>Short stable token for the current art URL. Empty when there is no art.</summary>
    public static string ArtRevision(string? albumArtUri)
    {
        if (string.IsNullOrWhiteSpace(albumArtUri))
            return "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(albumArtUri.Trim()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>Fixed-time compare. A wrong length still does not throw and does not match.</summary>
    public static bool PinMatches(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
            return false;
        var a = Encoding.UTF8.GetBytes(provided ?? "");
        var b = Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length)
        {
            CryptographicOperations.FixedTimeEquals(b, b);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// The address typed on the phone. A pasted tray URL (http://192.168.1.10:42342)
    /// is accepted and its port wins. https is rejected.
    /// </summary>
    public static bool TryParseAddress(string? hostField, string? portField, out string host, out int port, out string? error)
    {
        host = "";
        port = RemoteApi.DefaultPort;
        var raw = (hostField ?? "").Trim();
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = "Use the http address from the tray.";
            return false;
        }

        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            raw = raw["http://".Length..];
        raw = raw.Trim().TrimEnd('/');

        string? portText = null;
        var colon = raw.IndexOf(':');
        if (colon >= 0)
        {
            portText = raw[(colon + 1)..];
            raw = raw[..colon];
            if (portText.Contains('/') || raw.Contains('/'))
            {
                error = "Enter the PC address, and the port in the port box.";
                return false;
            }
        }
        else if (raw.Contains('/'))
        {
            error = "Enter the PC address, and the port in the port box.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsWhiteSpace))
        {
            error = "Enter the PC address.";
            return false;
        }

        portText ??= (portField ?? "").Trim();
        if (string.IsNullOrEmpty(portText))
            port = RemoteApi.DefaultPort;
        else if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1024 or > 65535)
        {
            error = "Port must be between 1024 and 65535.";
            return false;
        }

        host = raw;
        error = null;
        return true;
    }

    public static bool IsSixDigitPin(string? pin) =>
        pin is { Length: 6 } && pin.All(char.IsDigit);

    private static List<IPAddress> Parse(IEnumerable<string>? values)
    {
        var list = new List<IPAddress>();
        if (values is null)
            return list;
        foreach (var raw in values)
        {
            if (IPAddress.TryParse(raw?.Trim(), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                list.Add(ip);
        }

        return list;
    }
}

public sealed record RemoteStatusResponse(
    bool Ok,
    string Version,
    string? ActiveRoom,
    string? ActiveGroupLabel,
    IReadOnlyList<RemoteGroupDto> Groups,
    IReadOnlyList<string> Offline,
    RemoteNowPlayingDto? NowPlaying,
    int? Volume,
    bool? Muted,
    bool WakeActive,
    bool Busy,
    string? LiveGrouping = null,
    string? DailyGroupPreset = null,
    bool? GroupedAsDaily = null);

public sealed record RemoteGroupDto(string Room, string Label, int Members);

public sealed record RemoteNowPlayingDto(
    string? Title,
    string? Artist,
    string? Album,
    string State,
    string? Source,
    int? Track,
    int? Tracks,
    bool HasArt,
    string ArtRevision,
    int? PositionSeconds = null,
    int? DurationSeconds = null);

public sealed record RemoteCommandResponse(bool Ok, string? Message, string? Error);

public sealed record RemoteVolumeSampleResponse(bool Ok, int? Volume, bool? Muted, string? Error);

public sealed record RemoteSourcesResponse(
    bool Ok,
    RemoteDailyDto Daily,
    IReadOnlyList<RemoteSlotDto> Slots,
    IReadOnlyList<RemoteTagDto> Tags,
    IReadOnlyList<RemoteGenreDto> Genres,
    IReadOnlyList<RemoteFolderDto> Folders,
    string ControlSource,
    string ControlSourceLabel);

public sealed record RemoteDailyDto(string Label);

public sealed record RemoteSlotDto(int Index, string Label, string Kind);

public sealed record RemoteTagDto(string Key, string Label);

public sealed record RemoteGenreDto(string Name, int Count);

public sealed record RemoteFolderDto(string Name, string Path, int Tracks, bool InDaily);

public sealed record RemoteSearchResponse(bool Ok, int Count, IReadOnlyList<RemoteTrackDto> Tracks);

public sealed record RemoteTrackDto(
    string Path,
    string? Title,
    string? Artist,
    string? Album,
    string? Genre,
    string? Tags);

public sealed class RemoteVolumeBody
{
    public int Percent { get; set; }
}

public sealed class RemoteVolumeStepBody
{
    public int Direction { get; set; }
}

public sealed class RemoteRoomBody
{
    public string? Room { get; set; }
}

public sealed class RemoteKeyBody
{
    public string? Key { get; set; }
}

public sealed class RemoteNameBody
{
    public string? Name { get; set; }
}

public sealed class RemotePathBody
{
    public string? Path { get; set; }
}

public sealed class RemoteTrackBody
{
    public string? Path { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
}
