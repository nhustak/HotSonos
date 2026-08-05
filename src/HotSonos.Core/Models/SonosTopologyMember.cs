namespace HotSonos.Core.Models;

/// <summary>
/// One ZoneGroupTopology member, including invisible bonded devices (Sub, stereo
/// pair mate, surrounds) that normal control lists omit.
/// </summary>
public sealed record SonosTopologyMember
{
    public required string RoomName { get; init; }
    public required string Uuid { get; init; }
    public required string IpAddress { get; init; }
    public required string CoordinatorUuid { get; init; }
    public required string CoordinatorIpAddress { get; init; }
    public required string GroupId { get; init; }

    /// <summary>True for Sub, stereo mate, surrounds, etc. (not shown as a room).</summary>
    public bool Invisible { get; init; }

    /// <summary>
    /// Bonded channel role from ChannelMapSet when known, e.g. SW, LF, RF, LR.
    /// Null for unbonded / unknown.
    /// </summary>
    public string? ChannelRole { get; init; }

    /// <summary>
    /// True when Sonos reports Ethernet link up (<c>EthLink=1</c>).
    /// Null if the topology XML did not include the attribute.
    /// </summary>
    public bool? EthLink { get; init; }

    /// <summary>
    /// True when the product Wi‑Fi radio is enabled (<c>WifiEnabled=1</c>).
    /// Null if unknown.
    /// </summary>
    public bool? WifiEnabled { get; init; }

    /// <summary>
    /// Raw Sonos <c>ConnectionType</c> from topology (e.g. 4 = Ethernet Wi‑Fi off, 5 = Wi‑Fi).
    /// Null if unknown.
    /// </summary>
    public int? ConnectionType { get; init; }

    /// <summary>
    /// Short product name from device description when enriched (e.g. Port, Era 100, One).
    /// Null until probe or if unknown.
    /// </summary>
    public string? ProductName { get; init; }

    public bool IsCoordinator =>
        string.Equals(Uuid, CoordinatorUuid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Member is part of a stereo pair / HT bond (ChannelMapSet / HTSatChanMapSet),
    /// including the primary, the RF mate, and the Sub.
    /// </summary>
    public bool IsBonded { get; init; }

    /// <summary>
    /// Invisible stereo mate / surround (not the Sub). Kept for styling.
    /// </summary>
    public bool IsBondedMate =>
        IsBonded
        && Invisible
        && !string.Equals(ChannelRole, "SW", StringComparison.OrdinalIgnoreCase)
        && !RoomName.Contains("Sub", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Coarse product kind for topology icons: Sub, Port, Amp, Speaker, Unknown.
    /// Bonded mates keep their real product kind (e.g. Era → Speaker); use
    /// <see cref="IsBondedMate"/> for the link badge.
    /// </summary>
    public string ProductKind
    {
        get
        {
            if (Invisible
                && (string.Equals(ChannelRole, "SW", StringComparison.OrdinalIgnoreCase)
                    || RoomName.Contains("Sub", StringComparison.OrdinalIgnoreCase)))
                return "Sub";

            var p = ProductName ?? "";
            if (p.Contains("Port", StringComparison.OrdinalIgnoreCase)
                || p.Contains("Connect", StringComparison.OrdinalIgnoreCase))
                return "Port";
            if (p.Contains("Amp", StringComparison.OrdinalIgnoreCase))
                return "Amp";
            if (p.Contains("Sub", StringComparison.OrdinalIgnoreCase))
                return "Sub";
            if (p.Length > 0)
                return "Speaker";
            // Heuristic fallbacks when description not loaded yet
            if (RoomName.Contains("Theater", StringComparison.OrdinalIgnoreCase)
                || RoomName.Contains("Port", StringComparison.OrdinalIgnoreCase)
                || RoomName.Contains("Media", StringComparison.OrdinalIgnoreCase))
                return "Port";
            // Invisible stereo mate without product probe yet — still a speaker.
            if (IsBondedMate)
                return "Speaker";
            return "Unknown";
        }
    }

    /// <summary>Asset key for product kind icon (AppIcons).</summary>
    public string ProductIconKey => ProductKind switch
    {
        "Sub" => "sub",
        "Port" => "port",
        "Amp" => "amp",
        "Speaker" => "speaker",
        _ => "speaker",
    };

    /// <summary>Asset key for connection icon, or null if unknown.</summary>
    public string? ConnectionIconKey => ConnectionLabel switch
    {
        "ETH" => "ethernet",
        "Wi‑Fi" => "wifi",
        _ => null,
    };

    /// <summary>
    /// Short connection badge for UI: <c>ETH</c>, <c>Wi‑Fi</c>, or null if unknown.
    /// Wired wins when EthLink is up (includes Ethernet with Wi‑Fi disabled).
    /// </summary>
    public string? ConnectionLabel
    {
        get
        {
            if (EthLink == true)
                return "ETH";
            if (EthLink == false)
                return "Wi‑Fi";
            // Fallback when EthLink missing: ConnectionType 4 = Ethernet (WiFi Disabled), 5 = WiFi,
            // 1 = SonosNet (Ethernet) — treat 1 and 4 as wired.
            if (ConnectionType is 1 or 4)
                return "ETH";
            if (ConnectionType is 5)
                return "Wi‑Fi";
            if (WifiEnabled == true)
                return "Wi‑Fi";
            if (WifiEnabled == false && ConnectionType is not null)
                return "ETH";
            return null;
        }
    }

    /// <summary>Tooltip detail for connection (Ethernet link / Wi‑Fi radio / type code).</summary>
    public string ConnectionDetail
    {
        get
        {
            var parts = new List<string>();
            if (EthLink == true)
                parts.Add("Ethernet link up");
            else if (EthLink == false)
                parts.Add("No Ethernet link");
            if (WifiEnabled == true)
                parts.Add("Wi‑Fi radio on");
            else if (WifiEnabled == false)
                parts.Add("Wi‑Fi radio off");
            if (ConnectionType is int ct)
                parts.Add($"ConnectionType={ct}");
            return parts.Count == 0 ? "Connection unknown" : string.Join(" · ", parts);
        }
    }

    /// <summary>Short label for logs/UI: "Sub (SW)" or "Theater". Bonded is icon-only (no "bonded" text).</summary>
    public string DisplayLabel
    {
        get
        {
            var role = string.IsNullOrWhiteSpace(ChannelRole) ? null : ChannelRole.Trim().ToUpperInvariant();
            string baseLabel;
            if (Invisible)
            {
                // Link/bonded badge is the icon — don't put the word "bonded" in the chip.
                baseLabel = role is null ? RoomName : $"{RoomName} ({role})";
            }
            else
            {
                baseLabel = role is null ? RoomName : $"{RoomName} ({role})";
            }

            // Product short name when known (Port / Era 100) — icons added by UI.
            if (!string.IsNullOrWhiteSpace(ProductName)
                && !baseLabel.Contains(ProductName, StringComparison.OrdinalIgnoreCase))
                baseLabel = $"{baseLabel} · {ProductName}";

            var conn = ConnectionLabel;
            return conn is null ? baseLabel : $"{baseLabel} · {conn}";
        }
    }
}
