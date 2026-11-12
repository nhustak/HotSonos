using System.Security;
using System.Text.RegularExpressions;

namespace HotSonos.Core;

/// <summary>
/// A Spotify item the speaker can play because Spotify is already linked in the
/// Sonos app. The speaker fills in its own account token from the DIDL desc.
/// </summary>
public sealed record SpotifyItem(string Kind, string Id, string? Title)
{
    /// <summary>Spotify descriptor id 12 maps to service type 3079 (id &lt;&lt; 8 | 7).</summary>
    public const int DefaultServiceType = 3079;

    public string Canonical => $"spotify:{Kind}:{Id}";

    public static bool TryParse(string? text, out SpotifyItem? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = SpotifyRefRegex().Match(text.Trim());
        if (!match.Success)
            return false;

        var kind = match.Groups[1].Value.ToLowerInvariant();
        var id = match.Groups[2].Value;
        if (!PlaybackMagic.ContainsKey(kind) || id.Length == 0)
            return false;

        item = new SpotifyItem(kind, id, null);
        return true;
    }

    public SpotifyPlayback ToPlayback(int serviceType = DefaultServiceType)
    {
        if (!PlaybackMagic.TryGetValue(Kind, out var magic))
            throw new InvalidOperationException($"Spotify kind '{Kind}' cannot be played.");
        if (serviceType <= 0)
            serviceType = DefaultServiceType;

        // Containers are queued as x-rincon-cpcontainer with encoded colons.
        // Tracks use the raw x-sonos-spotify form this firmware actually plays.
        var spotifyRef = magic.IsContainer
            ? $"spotify%3a{Kind}%3a{Id}"
            : $"spotify:{Kind}:{Id}";
        var uri = magic.Prefix + spotifyRef;
        var title = string.IsNullOrWhiteSpace(Title) ? Canonical : Title.Trim();
        var escapedTitle = SecurityElement.Escape(title) ?? "";
        var escapedUri = SecurityElement.Escape(uri) ?? "";
        var itemId = magic.Key + spotifyRef;
        var metadata =
            "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
            "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" " +
            "xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
            $"<item id=\"{itemId}\" parentID=\"-1\" restricted=\"true\">" +
            $"<res protocolInfo=\"{magic.Protocol}\">{escapedUri}</res>" +
            $"<dc:title>{escapedTitle}</dc:title>" +
            $"<upnp:class>{magic.UpnpClass}</upnp:class>" +
            "<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">" +
            $"SA_RINCON{serviceType}_X_#Svc{serviceType}-0-Token</desc>" +
            "</item></DIDL-Lite>";

        return new SpotifyPlayback(uri, metadata, magic.IsContainer);
    }

    private static readonly Dictionary<string, (string Prefix, string Key, string UpnpClass, string Protocol, bool IsContainer)> PlaybackMagic =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["playlist"] = ("x-rincon-cpcontainer:1006206c", "1006206c", "object.container.playlistContainer", "x-rincon-cpcontainer:*:*:*", true),
            ["album"] = ("x-rincon-cpcontainer:1004206c", "00040000", "object.container.album.musicAlbum", "x-rincon-cpcontainer:*:*:*", true),
            ["show"] = ("x-rincon-cpcontainer:1006206c", "1006206c", "object.container.playlistContainer", "x-rincon-cpcontainer:*:*:*", true),
            ["track"] = ("x-sonos-spotify:", "00032020", "object.item.audioItem.musicTrack", "sonos.com-spotify:*:audio/mp3:*", false),
            ["episode"] = ("x-sonos-spotify:", "00032020", "object.item.audioItem.musicTrack", "sonos.com-spotify:*:audio/mp3:*", false),
        };

    private static Regex SpotifyRefRegex() => new(
        @"spotify.*[:/](album|episode|playlist|show|track)[:/]([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>URI and DIDL for <c>SetAVTransportURI</c>.</summary>
public sealed record SpotifyPlayback(string Uri, string Metadata, bool IsContainer);
