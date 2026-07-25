namespace HotSonos.App.Library;

/// <summary>Converts Sonos <c>x-file-cifs</c> URIs ↔ Windows UNC paths.</summary>
public static class SonosPath
{
    public const string CifsPrefix = "x-file-cifs://";

    public static bool TryToUnc(string? uriOrPath, out string unc)
    {
        unc = "";
        if (string.IsNullOrWhiteSpace(uriOrPath))
            return false;

        var s = uriOrPath.Trim();
        if (!s.StartsWith(CifsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (s.StartsWith(@"\\", StringComparison.Ordinal))
            {
                unc = s.Replace('/', '\\');
                return true;
            }
            return false;
        }

        try
        {
            var decoded = Uri.UnescapeDataString(s[CifsPrefix.Length..]);
            decoded = decoded.Replace('/', '\\').TrimStart('\\');
            if (string.IsNullOrWhiteSpace(decoded))
                return false;
            unc = @"\\" + decoded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// UNC or already-cifs URI → <c>x-file-cifs://host/share/path</c> for Sonos enqueue.
    /// </summary>
    public static bool TryToCifsUri(string? uriOrPath, out string cifsUri)
    {
        cifsUri = "";
        if (string.IsNullOrWhiteSpace(uriOrPath))
            return false;

        var s = uriOrPath.Trim();
        if (s.StartsWith(CifsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            cifsUri = s;
            return true;
        }

        if (!TryToUnc(s, out var unc))
            return false;

        // \\host\share\path → host/share/path (URI-escaped segments)
        var body = unc.TrimStart('\\').Replace('\\', '/');
        var parts = body.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var encoded = string.Join('/', parts.Select(p => Uri.EscapeDataString(p)));
        cifsUri = CifsPrefix + encoded;
        return true;
    }
}
