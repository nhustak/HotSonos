namespace HotSonos.Activity;

public sealed class SeedHistoryEntry
{
    public string? Uri { get; init; }
    public DateTime Utc { get; init; }
    public string? Kind { get; init; }
}

public sealed class SeedPlayEvent
{
    public DateTime Utc { get; init; }
    public string? Kind { get; init; }
    public string? Uri { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Source { get; init; }
}

public sealed class SeededSession
{
    public required string TrackKey { get; init; }
    public string? Uri { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Source { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DateTime EndedUtc { get; init; }
    public required string Outcome { get; init; }
    public required string Trigger { get; init; }
}

/// <summary>
/// One-time import. Play-event rows (started/skipped/stopped/previous) become
/// sessions when they can be paired. Last-played history adds a song only when
/// that song never appears in the event import — history keeps a single timestamp
/// per track for shuffle, so it cannot reconstruct every play.
/// </summary>
public static class ActivitySeed
{
    public static IReadOnlyList<SeededSession> Build(
        IEnumerable<SeedHistoryEntry>? history,
        IEnumerable<SeedPlayEvent>? events)
    {
        var sessions = PairEvents(events ?? []);
        var keys = new HashSet<string>(
            sessions.Select(s => s.TrackKey),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in (history ?? []).OrderByDescending(h => h.Utc))
        {
            if (!string.IsNullOrWhiteSpace(entry.Kind)
                && !entry.Kind.Equals("played", StringComparison.OrdinalIgnoreCase))
                continue;
            var key = TrackKeys.Normalize(entry.Uri);
            if (key.Length == 0 || !keys.Add(key))
                continue;
            sessions.Add(new SeededSession
            {
                TrackKey = key,
                Uri = entry.Uri?.Trim(),
                Title = TrackKeys.FileTitle(entry.Uri),
                Source = SourceFromUri(entry.Uri),
                StartedUtc = SpecifyUtc(entry.Utc),
                EndedUtc = SpecifyUtc(entry.Utc),
                Outcome = PlayOutcomes.Unknown,
                Trigger = PlayTriggers.SeedLastPlayed,
            });
        }

        return sessions
            .OrderBy(s => s.StartedUtc)
            .ToList();
    }

    private static List<SeededSession> PairEvents(IEnumerable<SeedPlayEvent> events)
    {
        var sessions = new List<SeededSession>();
        SeededSession? open = null;
        foreach (var evt in events.OrderBy(e => e.Utc))
        {
            var kind = (evt.Kind ?? "").Trim().ToLowerInvariant();
            if (kind is not ("started" or "skipped" or "stopped" or "previous"))
                continue;

            var draft = Draft(evt);
            if (draft is null)
                continue;

            switch (kind)
            {
                case "started":
                    // A new start supersedes an unpaired start. The earlier one never
                    // closed in the debug log; leave it out rather than invent an ending.
                    open = draft;
                    break;
                case "skipped":
                    if (open is null)
                        break;
                    sessions.Add(Finish(open, evt.Utc, PlayOutcomes.Skipped,
                        evt.Source?.Equals("hotkey", StringComparison.OrdinalIgnoreCase) == true
                            ? PlayTriggers.HotkeyNext
                            : PlayTriggers.ControllerNext));
                    open = null;
                    break;
                case "stopped":
                    if (open is null)
                        break;
                    sessions.Add(Finish(open, evt.Utc, PlayOutcomes.Stopped, PlayTriggers.Stop));
                    open = null;
                    break;
                case "previous":
                    if (open is null)
                        break;
                    sessions.Add(Finish(open, evt.Utc, PlayOutcomes.Interrupted,
                        evt.Source?.Equals("hotkey", StringComparison.OrdinalIgnoreCase) == true
                            ? PlayTriggers.HotkeyPrevious
                            : PlayTriggers.TrackChanged));
                    open = null;
                    break;
            }
        }

        return sessions;
    }

    private static SeededSession? Draft(SeedPlayEvent evt)
    {
        var key = TrackKeys.ForPlay(evt.Uri, evt.Title, TrackKeys.TitleIsIdentity(evt.Uri));
        if (key.Length == 0)
            return null;
        return new SeededSession
        {
            TrackKey = key,
            Uri = string.IsNullOrWhiteSpace(evt.Uri) ? null : evt.Uri.Trim(),
            Title = string.IsNullOrWhiteSpace(evt.Title) ? TrackKeys.FileTitle(evt.Uri) : evt.Title.Trim(),
            Artist = string.IsNullOrWhiteSpace(evt.Artist) ? null : evt.Artist.Trim(),
            Source = SourceFromUri(evt.Uri),
            StartedUtc = SpecifyUtc(evt.Utc),
            EndedUtc = SpecifyUtc(evt.Utc),
            Outcome = PlayOutcomes.Unknown,
            Trigger = PlayTriggers.SeedPlayEvent,
        };
    }

    private static SeededSession Finish(SeededSession open, DateTime endedUtc, string outcome, string trigger) =>
        new()
        {
            TrackKey = open.TrackKey,
            Uri = open.Uri,
            Title = open.Title,
            Artist = open.Artist,
            Source = open.Source,
            StartedUtc = open.StartedUtc,
            EndedUtc = SpecifyUtc(endedUtc),
            Outcome = outcome,
            Trigger = trigger,
        };

    private static DateTime SpecifyUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    internal static string SourceFromUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return "other";
        var u = uri.ToLowerInvariant();
        if (u.Contains("file-cifs") || u.Contains("file-smb") || u.StartsWith(@"\\")
            || u.EndsWith(".flac") || u.EndsWith(".mp3") || u.EndsWith(".m4a"))
            return "library";
        if (u.Contains("spotify"))
            return "spotify";
        if (u.Contains("tunein") || u.Contains("x-rincon-mp3radio") || u.Contains("x-sonosapi-stream"))
            return "radio";
        if (u.Contains("spdif") || u.Contains("x-sonos-htastream") || u.Contains("x-rincon-stream"))
            return "linein";
        return "other";
    }
}
