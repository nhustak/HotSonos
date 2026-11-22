namespace HotSonos.Core;

/// <summary>
/// Play-history exclusion versus tracks that are still waiting on the queue.
/// ShuffleExcludePlayed controls recently heard tracks only. A top-up always
/// receives <see cref="QueuedUris"/> (possibly empty) so waiting tracks stay
/// out even when that setting is off. A fresh shuffle leaves
/// <see cref="QueuedUris"/> null and keeps the previous history stamp.
/// </summary>
public sealed record ShuffleExclusion(
    IReadOnlyCollection<string>? ExcludeUris,
    IReadOnlyDictionary<string, DateTime>? PlayedAtUtc,
    IReadOnlyCollection<string>? QueuedUris)
{
    /// <summary>
    /// Build the exclusion inputs for one shuffle.
    /// Keys in both maps are already normalized the same way
    /// (<c>PlayHistoryStore.NormalizeKey</c>).
    /// A session key is still waiting when it has no play time, or the play
    /// time is earlier than the enqueue time.
    /// </summary>
    public static ShuffleExclusion Build(
        bool excludePlayed,
        bool topUp,
        IReadOnlyDictionary<string, DateTime>? lastPlayedUtcByKey,
        IReadOnlyDictionary<string, DateTime>? sessionServedAtUtc,
        DateTime utcNow)
    {
        var played = Copy(lastPlayedUtcByKey);
        var served = Copy(sessionServedAtUtc);

        if (!topUp)
        {
            if (!excludePlayed)
                return new ShuffleExclusion(null, null, null);

            foreach (var key in served.Keys)
                played[key] = utcNow;
            return new ShuffleExclusion(played.Keys.ToList(), played, null);
        }

        var queued = new List<string>();
        foreach (var (key, enqueuedAt) in served)
        {
            if (!played.TryGetValue(key, out var when) || when < enqueuedAt)
                queued.Add(key);
        }

        if (!excludePlayed)
            return new ShuffleExclusion(null, null, queued);

        return new ShuffleExclusion(played.Keys.ToList(), played, queued);
    }

    private static Dictionary<string, DateTime> Copy(IReadOnlyDictionary<string, DateTime>? source)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
            return map;
        foreach (var (key, when) in source)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (!map.TryGetValue(key, out var existing) || when > existing)
                map[key] = when;
        }

        return map;
    }
}
