namespace HotSonos.App.Library;

/// <summary>Converts Sonos <c>x-file-cifs</c> URIs to Windows UNC paths.</summary>
public static class SonosPath
{
    public static bool TryToUnc(string? uriOrPath, out string unc)
    {
        unc = "";
        if (string.IsNullOrWhiteSpace(uriOrPath))
            return false;

        const string prefix = "x-file-cifs://";
        var s = uriOrPath.Trim();
        if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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
            var decoded = Uri.UnescapeDataString(s[prefix.Length..]);
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
}
