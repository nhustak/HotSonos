namespace HotSonos.App.Library;

/// <summary>Field-restricted library search (one enhanced form at a time).</summary>
public enum LibrarySearchField
{
    /// <summary>Default: title/artist/album/genre/path/tags, etc.</summary>
    All,
    Title,
    Artist,
    Tags,
    Format,
}

/// <summary>
/// Parses optional prefixes:
/// <list type="bullet">
/// <item><c>T:</c> title only</item>
/// <item><c>A:</c> artist only</item>
/// <item><c>TG:</c> tags only (comma/semicolon/space-separated labels or keys)</item>
/// <item><c>F:</c> format only (codec / extension, e.g. flac, mp3)</item>
/// </list>
/// Only one prefix is supported; first match wins. <c>TG:</c> is checked before <c>T:</c>.
/// </summary>
public static class LibrarySearchQuery
{
    public static (LibrarySearchField Field, string? Term) Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (LibrarySearchField.All, null);

        var q = query.Trim();

        if (TryPrefix(q, "TG:", out var tags))
            return (LibrarySearchField.Tags, tags);
        if (TryPrefix(q, "T:", out var title))
            return (LibrarySearchField.Title, title);
        if (TryPrefix(q, "A:", out var artist))
            return (LibrarySearchField.Artist, artist);
        if (TryPrefix(q, "F:", out var format))
            return (LibrarySearchField.Format, format);

        return (LibrarySearchField.All, q);
    }

    /// <summary>Split a TG: term into individual tag tokens (labels or keys).</summary>
    public static IReadOnlyList<string> SplitTagList(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];
        return term.Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    private static bool TryPrefix(string query, string prefix, out string? term)
    {
        term = null;
        if (!query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        term = query[prefix.Length..].Trim();
        if (term.Length == 0)
            term = null;
        return true;
    }
}
