namespace HotSonos.Remote.Protocol;

public sealed record LibraryRootEdit(string Path, bool InDaily);

/// <summary>Phone edits to the tray library folders. Per-folder master maps stay on the PC.</summary>
public static class LibraryPathRules
{
    public static bool TryNormalize(
        IEnumerable<LibraryRootEdit>? roots,
        string? master,
        out IReadOnlyList<LibraryRootEdit> cleanRoots,
        out string? cleanMaster,
        out string? error)
    {
        var list = new List<LibraryRootEdit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots ?? [])
        {
            if (!TryOne(root.Path, out var path, out error))
            {
                cleanRoots = [];
                cleanMaster = null;
                return false;
            }

            if (path.Length == 0 || !seen.Add(path))
                continue;
            list.Add(new LibraryRootEdit(path, root.InDaily));
        }

        if (!TryOne(master, out var masterPath, out error))
        {
            cleanRoots = [];
            cleanMaster = null;
            return false;
        }

        cleanRoots = list;
        cleanMaster = masterPath.Length == 0 ? null : masterPath;
        error = null;
        return true;
    }

    /// <summary>
    /// One shared master can be set from the phone. Differing per-folder masters are left as they are
    /// unless the requested value still matches the first one.
    /// </summary>
    public static bool MasterEditAllowed(int distinctMasters, string? currentMaster, string? requestedMaster)
    {
        if (distinctMasters <= 1)
            return true;
        return string.Equals(Normalize(currentMaster), Normalize(requestedMaster), StringComparison.OrdinalIgnoreCase);
    }

    static bool TryOne(string? raw, out string path, out string? error)
    {
        path = Normalize(raw);
        error = null;
        if (path.Length == 0)
            return true;
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = "Library folders are paths on the PC (\\\\server\\share or a drive), not a web address.";
            return false;
        }

        if (path.IndexOfAny(['*', '?', '\n', '\r']) >= 0)
        {
            error = "That folder path has a character this list cannot store.";
            return false;
        }

        return true;
    }

    static string Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim().TrimEnd('\\', '/');
}
