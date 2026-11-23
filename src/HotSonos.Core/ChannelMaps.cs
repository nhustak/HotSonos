namespace HotSonos.Core;

/// <summary>Parse and compare Sonos ChannelMapSet strings. Keeps exact tokens; does not invent formats.</summary>
public static class ChannelMaps
{
    public static IReadOnlyList<(string Uuid, string Roles)> Parse(string? map)
    {
        if (string.IsNullOrWhiteSpace(map))
            return [];

        var parts = new List<(string, string)>();
        foreach (var part in map.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
                continue;
            var uuid = part[..colon].Trim();
            var roles = part[(colon + 1)..].Trim();
            if (uuid.Length == 0 || roles.Length == 0)
                continue;
            parts.Add((uuid, roles));
        }

        return parts;
    }

    public static string FirstRole(string roles)
    {
        var comma = roles.IndexOf(',');
        return comma < 0 ? roles.Trim() : roles[..comma].Trim();
    }

    public static bool IsSubRole(string roles) =>
        string.Equals(FirstRole(roles), "SW", StringComparison.OrdinalIgnoreCase);

    public static bool IsRightRole(string roles)
    {
        var first = FirstRole(roles);
        return string.Equals(first, "RF", StringComparison.OrdinalIgnoreCase)
               || string.Equals(first, "R", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLeftOrPrimaryRole(string roles)
    {
        var first = FirstRole(roles);
        return string.Equals(first, "LF", StringComparison.OrdinalIgnoreCase)
               || string.Equals(first, "L", StringComparison.OrdinalIgnoreCase)
               || string.Equals(first, "LF,RF", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasSub(string? map) => Parse(map).Any(p => IsSubRole(p.Roles));

    public static string? SubUuid(string? map) =>
        Parse(map).FirstOrDefault(p => IsSubRole(p.Roles)).Uuid is { Length: > 0 } uuid ? uuid : null;

    public static string? RightUuid(string? map) =>
        Parse(map).FirstOrDefault(p => IsRightRole(p.Roles)).Uuid is { Length: > 0 } uuid ? uuid : null;

    public static string? PrimaryUuid(string? map)
    {
        var parts = Parse(map);
        var left = parts.FirstOrDefault(p => !IsSubRole(p.Roles) && !IsRightRole(p.Roles));
        if (left.Uuid is { Length: > 0 })
            return left.Uuid;
        return parts.FirstOrDefault(p => !IsSubRole(p.Roles)).Uuid is { Length: > 0 } uuid ? uuid : null;
    }

    public static string WithoutSub(string? map)
    {
        var kept = Parse(map).Where(p => !IsSubRole(p.Roles)).ToList();
        return string.Join(";", kept.Select(p => $"{p.Uuid}:{p.Roles}"));
    }

    public static string WithSub(string? existingOrPairMap, string primaryUuid, string? rightUuid, string subUuid)
    {
        var baseMap = WithoutSub(existingOrPairMap);
        if (string.IsNullOrWhiteSpace(baseMap))
        {
            baseMap = string.IsNullOrWhiteSpace(rightUuid)
                ? $"{primaryUuid}:LF,RF"
                : $"{primaryUuid}:LF,LF;{rightUuid}:RF,RF";
        }

        return $"{baseMap};{subUuid}:SW";
    }

    public static bool EqualsMap(string? a, string? b)
    {
        var left = (a ?? "").Trim();
        var right = (b ?? "").Trim();
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>
    /// Desired can be reached with one additive CreateStereoPair (current is the
    /// pair/standalone portion of desired; desired also has a Sub).
    /// </summary>
    public static bool CanAdditivelyReach(string? current, string? desired) =>
        !string.IsNullOrWhiteSpace(current)
        && !EqualsMap(current, desired)
        && HasSub(desired)
        && !HasSub(current)
        && EqualsMap(WithoutSub(desired), current);
}
