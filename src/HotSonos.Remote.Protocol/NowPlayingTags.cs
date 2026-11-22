namespace HotSonos.Remote.Protocol;

/// <summary>
/// Catalog tags for the track that is playing. The phone and the tray share this shape.
/// <see cref="CanTag"/> is false when the track is not a library file the PC can write.
/// </summary>
public sealed record RemoteNowTagsResponse(
    bool Ok,
    bool CanTag,
    string? Path,
    string? Message,
    IReadOnlyList<RemoteNowTagDto> Tags);

public sealed record RemoteNowTagDto(string Key, string Label, bool On);

public static class NowPlayingTags
{
    public static RemoteNowTagsResponse Describe(
        string? path,
        string? blockedMessage,
        IEnumerable<(string Key, string Label)> catalog,
        IEnumerable<string>? onKeys)
    {
        var on = new HashSet<string>(onKeys ?? [], StringComparer.OrdinalIgnoreCase);
        var tags = new List<RemoteNowTagDto>();
        foreach (var (key, label) in catalog)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            var shown = string.IsNullOrWhiteSpace(label) ? key : label;
            tags.Add(new RemoteNowTagDto(key, shown, on.Contains(key)));
        }

        var canTag = string.IsNullOrWhiteSpace(blockedMessage) && !string.IsNullOrWhiteSpace(path);
        return new RemoteNowTagsResponse(true, canTag, canTag ? path : null, blockedMessage, tags);
    }
}
