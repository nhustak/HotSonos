namespace HotSonos.Remote.Protocol;

/// <summary>The queue row after the one that is playing. Null when the phone has not read a following row.</summary>
public sealed record RemoteUpcomingDto(string? Title, string? Artist);

/// <summary>Picks the queue row at the playing position plus one. Queue numbers are 1-based.</summary>
public static class QueueCursor
{
    public static RemoteUpcomingDto? NextAfter(int? currentNumber, IReadOnlyList<RemoteQueueItemDto>? tracks)
    {
        if (currentNumber is not int current || current < 1 || tracks is null)
            return null;

        var want = current + 1;
        foreach (var track in tracks)
        {
            if (track.Number != want)
                continue;
            if (string.IsNullOrWhiteSpace(track.Title))
                return null;
            var artist = string.IsNullOrWhiteSpace(track.Artist) ? null : track.Artist.Trim();
            return new RemoteUpcomingDto(track.Title.Trim(), artist);
        }

        return null;
    }
}

/// <summary>
/// What to show the moment Next is pressed, before the speaker poll catches up.
/// A snapshot that still names the track we left is stale for <see cref="HoldMs"/>.
/// </summary>
public static class SkipAhead
{
    public const int HoldMs = 4000;

    public readonly record struct Face(string Title, string Artist);

    public static Face? OnNext(RemoteUpcomingDto? upcoming)
    {
        if (string.IsNullOrWhiteSpace(upcoming?.Title))
            return null;
        return new Face(upcoming.Title.Trim(), upcoming.Artist?.Trim() ?? "");
    }

    public static bool IsStale(string? skippedTitle, string? incomingTitle, long ageMs)
    {
        if (string.IsNullOrWhiteSpace(skippedTitle))
            return false;
        if (ageMs < 0 || ageMs >= HoldMs)
            return false;
        var incoming = incomingTitle?.Trim() ?? "";
        // A skip often reports the old title, or nothing, until the speaker moves.
        if (incoming.Length == 0 || incoming.Equals("Nothing playing", StringComparison.Ordinal))
            return true;
        return string.Equals(incoming, skippedTitle.Trim(), StringComparison.Ordinal);
    }
}
