namespace HotSonos.Core;

/// <summary>
/// A Sonos UPnP service: its SOAP type URN and control URL path (relative to
/// http://{ip}:1400). These paths are stable across S1/S2 firmware.
/// </summary>
public sealed record SonosService(string Type, string ControlPath)
{
    public static readonly SonosService AvTransport =
        new("urn:schemas-upnp-org:service:AVTransport:1", "/MediaRenderer/AVTransport/Control");

    public static readonly SonosService RenderingControl =
        new("urn:schemas-upnp-org:service:RenderingControl:1", "/MediaRenderer/RenderingControl/Control");

    public static readonly SonosService ContentDirectory =
        new("urn:schemas-upnp-org:service:ContentDirectory:1", "/MediaServer/ContentDirectory/Control");

    public static readonly SonosService ZoneGroupTopology =
        new("urn:schemas-upnp-org:service:ZoneGroupTopology:1", "/ZoneGroupTopology/Control");
}
