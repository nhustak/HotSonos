using System.IO;
using System.Text.Json;

namespace HotSonos.App.Services;

/// <summary>One speaker reachability transition.</summary>
public sealed class SpeakerOutageEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary><c>down</c> or <c>up</c>.</summary>
    public required string Kind { get; init; }

    public required string Room { get; init; }
    public required string Ip { get; init; }

    /// <summary>True when this speaker was the group coordinator at the time.</summary>
    public bool IsCoordinator { get; init; }

    /// <summary>Group label at the time, for context on what the outage took down.</summary>
    public string? Group { get; init; }

    /// <summary>How long the speaker was unreachable. Only set on <c>up</c>.</summary>
    public double? DownSeconds { get; init; }

    /// <summary>Consecutive failed probes before recovery. Only set on <c>up</c>.</summary>
    public int? MissedProbes { get; init; }

    public string Describe() => Kind == "down"
        ? $"⛔ {Room} ({Ip}) stopped responding{(IsCoordinator ? " — IT IS THE COORDINATOR" : "")}"
        : $"✅ {Room} ({Ip}) came back after {DownSeconds:F0}s ({MissedProbes} missed probe(s))";
}

/// <summary>
/// Durable trail of speakers going unreachable and coming back. Sonos membership
/// keeps listing a speaker that has stopped answering, so an outage otherwise
/// leaves no trace once it recovers — this is the record that survives it.
/// Append-only JSONL under %LocalAppData%\HotSonos\speaker-outages.jsonl + ring.
/// </summary>
public sealed class SpeakerOutageLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<SpeakerOutageEvent> _ring = [];
    private const int RingCapacity = 400;
    private const int MaxFileLines = 8000;

    public SpeakerOutageLog(string? path = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "speaker-outages.jsonl");
    }

    public string FilePath => _path;

    public event Action<SpeakerOutageEvent>? Recorded;

    public IReadOnlyList<SpeakerOutageEvent> Recent
    {
        get { lock (_gate) return _ring.ToList(); }
    }

    public void Record(SpeakerOutageEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        lock (_gate)
        {
            _ring.Add(ev);
            if (_ring.Count > RingCapacity)
                _ring.RemoveRange(0, _ring.Count - RingCapacity);

            try
            {
                File.AppendAllText(_path, JsonSerializer.Serialize(ev, JsonOptions) + Environment.NewLine);
                TrimIfHugeUnlocked();
            }
            catch
            {
                // Never let a disk hiccup take down the watcher.
            }
        }

        Recorded?.Invoke(ev);
    }

    private void TrimIfHugeUnlocked()
    {
        try
        {
            var lines = File.ReadAllLines(_path);
            if (lines.Length <= MaxFileLines)
                return;
            File.WriteAllLines(_path, lines.Skip(lines.Length - MaxFileLines / 2));
        }
        catch
        {
            // Trimming is best-effort.
        }
    }
}
