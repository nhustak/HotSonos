namespace HotSonos.Activity;

/// <summary>
/// Canonical track identity for the activity log. This is the same normalization
/// <c>PlayHistoryStore.NormalizeKey</c> uses for shuffle exclusion, so a seeded
/// last-played row and a live library play land on one song.
/// </summary>
public static class TrackKeys
{
    public static string Normalize(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return "";
        var s = uri.Trim();
        try { s = Uri.UnescapeDataString(s); } catch { /* keep */ }
        s = s.Replace('\\', '/');
        const string prefix = "x-file-cifs://";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            s = s[prefix.Length..];
        var q = s.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0) s = s[..q];
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>File name without extension, for seeded rows that have a URI but no title.</summary>
    public static string? FileTitle(string? uriOrKey)
    {
        var key = Normalize(uriOrKey);
        if (key.Length == 0)
            return null;
        var slash = key.LastIndexOf('/');
        var name = slash >= 0 && slash < key.Length - 1 ? key[(slash + 1)..] : key;
        var dot = name.LastIndexOf('.');
        if (dot > 0)
            name = name[..dot];
        return name.Length == 0 ? null : name;
    }
}
