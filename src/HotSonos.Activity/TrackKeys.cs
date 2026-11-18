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

    /// <summary>
    /// Library files and per-track services (Spotify and the like) are the URI.
    /// A shared stream URI is one station, so the title is part of the identity
    /// when we have one. A missing title keeps the bare URI so a metadata gap
    /// does not look like a different song — <see cref="SameTrack"/>.
    /// </summary>
    public static string ForPlay(string? uri, string? title, bool titleIsIdentity)
    {
        var key = Normalize(uri);
        if (!titleIsIdentity)
            return key;
        var t = (title ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0)
            return key;
        if (key.Length == 0)
            return "title:" + t;
        return key + "\n" + t;
    }

    /// <summary>
    /// True for radio and other shared-URI streams. Library files and per-track
    /// service URIs stay URI-only so they match shuffle's last-played key.
    /// </summary>
    public static bool TitleIsIdentity(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return true;
        var u = uri.Trim().ToLowerInvariant();
        if (u.Contains("file-cifs", StringComparison.Ordinal)
            || u.Contains("file-smb", StringComparison.Ordinal)
            || u.StartsWith('\\')
            || u.EndsWith(".flac", StringComparison.Ordinal)
            || u.EndsWith(".mp3", StringComparison.Ordinal)
            || u.EndsWith(".m4a", StringComparison.Ordinal)
            || u.EndsWith(".wav", StringComparison.Ordinal)
            || u.EndsWith(".aiff", StringComparison.Ordinal)
            || u.EndsWith(".aif", StringComparison.Ordinal))
            return false;
        if (u.Contains("spotify", StringComparison.Ordinal)
            || u.Contains("deezer", StringComparison.Ordinal)
            || u.Contains("dzr", StringComparison.Ordinal)
            || u.Contains("amazon", StringComparison.Ordinal)
            || u.Contains("apple", StringComparison.Ordinal)
            || u.Contains("itunes", StringComparison.Ordinal)
            || u.Contains("pandora", StringComparison.Ordinal))
            return false;
        return true;
    }

    /// <summary>
    /// Same song when the keys match, or when they share a stream URI and one of
    /// them has no title yet (the title arrived or dropped for a moment).
    /// Two different titles on the same station are different songs.
    /// </summary>
    public static bool SameTrack(string? openKey, string? snapshotKey)
    {
        if (string.IsNullOrWhiteSpace(openKey) || string.IsNullOrWhiteSpace(snapshotKey))
            return false;
        if (string.Equals(openKey, snapshotKey, StringComparison.OrdinalIgnoreCase))
            return true;
        var (openBase, openTitle) = Split(openKey);
        var (snapBase, snapTitle) = Split(snapshotKey);
        if (openBase.Length == 0 || snapBase.Length == 0)
            return false;
        if (!string.Equals(openBase, snapBase, StringComparison.OrdinalIgnoreCase))
            return false;
        if (openTitle.Length == 0 || snapTitle.Length == 0)
            return true;
        return string.Equals(openTitle, snapTitle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Keeps a titled stream key when a later sample omits the title.</summary>
    public static string Prefer(string current, string incoming)
    {
        var (_, currentTitle) = Split(current);
        var (_, incomingTitle) = Split(incoming);
        if (currentTitle.Length == 0 && incomingTitle.Length > 0)
            return incoming;
        return current;
    }

    private static (string Base, string Title) Split(string key)
    {
        if (key.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            return ("", key["title:".Length..]);
        var i = key.IndexOf('\n');
        if (i < 0)
            return (key, "");
        return (key[..i], key[(i + 1)..]);
    }
}
