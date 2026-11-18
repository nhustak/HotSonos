using Microsoft.Data.Sqlite;

namespace HotSonos.Activity;

/// <summary>
/// SQLite activity log. Not thread-safe — <see cref="ActivityLog"/> holds the lock.
/// Retention: <see cref="RetentionDays"/> plus a row cap so the file cannot grow without bound.
/// </summary>
public sealed class ActivityStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly int _retentionDays;
    private readonly int _maxEvents;
    private readonly int _maxSessions;
    private int _writesSincePrune;

    public ActivityStore(string path, int retentionDays = 180, int maxEvents = 80_000, int maxSessions = 40_000)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Database path is required.", nameof(path));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _retentionDays = Math.Clamp(retentionDays, 7, 3650);
        _maxEvents = Math.Clamp(maxEvents, 5, 500_000);
        _maxSessions = Math.Clamp(maxSessions, 5, 500_000);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        _conn = new SqliteConnection(builder.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        EnsureSchema();
        DatabasePath = path;
    }

    public string DatabasePath { get; }

    public bool HasMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM meta WHERE key = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() is not null;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO meta(key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void InsertSeedSessions(IReadOnlyList<SeededSession> sessions)
    {
        if (sessions.Count == 0)
            return;
        using var tx = _conn.BeginTransaction();
        foreach (var s in sessions)
            InsertSession(s.TrackKey, s.Uri, s.Title, s.Artist, null, null, s.Source,
                s.StartedUtc, s.EndedUtc, null, null, s.Outcome, s.Trigger, seeded: true, tx);
        tx.Commit();
        MaybePrune(force: true);
    }

    public void Persist(TrackerStep step)
    {
        if (step.Closed.Count == 0 && step.Events.Count == 0)
            return;
        using var tx = _conn.BeginTransaction();
        foreach (var s in step.Closed)
        {
            InsertSession(s.TrackKey, s.Uri, s.Title, s.Artist, s.Album, s.Zone, s.Source,
                s.StartedUtc, s.EndedUtc, s.ListenedSeconds, s.TrackSeconds, s.Outcome, s.Trigger,
                seeded: false, tx);
        }

        foreach (var e in step.Events)
            InsertEvent(e, tx);
        tx.Commit();
        MaybePrune(force: false);
    }

    public void InsertEvent(ActivityEventDraft draft)
    {
        using var tx = _conn.BeginTransaction();
        InsertEvent(draft, tx);
        tx.Commit();
        MaybePrune(force: false);
    }

    public IReadOnlyList<SongStatRow> SearchSongs(string? query, int limit)
    {
        limit = Math.Clamp(limit, 1, 500);
        var q = (query ?? "").Trim();
        var like = "%" + EscapeLike(q) + "%";
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT track_key,
                   COALESCE(NULLIF(MAX(title), ''), track_key) AS title,
                   COALESCE(MAX(artist), '') AS artist,
                   COUNT(*) AS play_count,
                   SUM(CASE WHEN outcome = 'skipped' THEN 1 ELSE 0 END) AS skip_count,
                   MAX(started_utc) AS last_played
            FROM play_sessions
            WHERE source NOT IN ('linein', 'empty')
              AND (
                    $q = ''
                    OR IFNULL(title, '') LIKE $like ESCAPE '\'
                    OR IFNULL(artist, '') LIKE $like ESCAPE '\'
                    OR track_key LIKE $like ESCAPE '\'
                  )
            GROUP BY track_key
            ORDER BY last_played DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$q", q);
        cmd.Parameters.AddWithValue("$like", like);
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<SongStatRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var last = ParseUtc(reader.IsDBNull(5) ? null : reader.GetString(5));
            rows.Add(new SongStatRow
            {
                TrackKey = reader.GetString(0),
                Title = reader.GetString(1),
                Artist = reader.GetString(2),
                PlayCount = Convert.ToInt32(reader.GetValue(3)),
                SkipCount = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
                LastPlayedUtc = last,
                LastPlayedLocal = last is DateTime utc ? ActivityText.LocalStamp(utc) : "",
            });
        }

        return rows;
    }

    public IReadOnlyList<PlaySessionRow> PlaysForSong(string trackKey, int limit)
    {
        limit = Math.Clamp(limit, 1, 2000);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT track_key, title, artist, album, zone, source, started_utc,
                   listened_seconds, track_seconds, outcome, trigger
            FROM play_sessions
            WHERE track_key = $key
            ORDER BY started_utc DESC, id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$key", trackKey);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadSessions(cmd);
    }

    public MusicReportData QueryMusic(DateTime? fromUtc, DateTime? toUtcExclusive, bool skippedOnly, int limit)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var plays = QueryPlays(fromUtc, toUtcExclusive, skippedOnly, limit);
        return new MusicReportData
        {
            Plays = plays,
            MostPlayed = Rank(fromUtc, toUtcExclusive, skipped: false, limit: 8),
            MostSkipped = Rank(fromUtc, toUtcExclusive, skipped: true, limit: 8),
            RecentlyPlayed = plays.Take(8).Select(p => new RankedSongRow
            {
                TrackKey = p.TrackKey,
                Title = string.IsNullOrWhiteSpace(p.Title) ? p.TrackKey : p.Title,
                Artist = p.Artist,
                Count = 1,
                Label = $"{p.WhenLocal}  {p.OutcomeLabel}",
            }).ToList(),
        };
    }

    public IReadOnlyList<ActivityEventRow> QueryEvents(string? category, string? kind, int limit)
    {
        limit = Math.Clamp(limit, 1, 2000);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, utc, category, kind, summary, zone
            FROM events
            WHERE ($cat = '' OR category = $cat)
              AND ($kind = '' OR kind = $kind)
            ORDER BY utc DESC, id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$cat", category?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$kind", kind?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<ActivityEventRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var utc = ParseUtc(reader.GetString(1)) ?? DateTime.UnixEpoch;
            var kindValue = reader.GetString(3);
            rows.Add(new ActivityEventRow
            {
                Id = reader.GetInt64(0),
                Utc = utc,
                WhenLocal = ActivityText.LocalStamp(utc),
                Category = reader.GetString(2),
                CategoryLabel = ActivityText.CategoryLabel(reader.GetString(2)),
                Kind = kindValue,
                KindLabel = kindValue.Replace('_', ' '),
                Summary = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Zone = reader.IsDBNull(5) ? "" : reader.GetString(5),
            });
        }

        return rows;
    }

    public IReadOnlyList<string> EventKinds(string? category)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT DISTINCT kind FROM events
            WHERE ($cat = '' OR category = $cat)
            ORDER BY kind;
            """;
        cmd.Parameters.AddWithValue("$cat", category?.Trim() ?? "");
        var kinds = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            kinds.Add(reader.GetString(0));
        return kinds;
    }

    public int CountSessions()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM play_sessions;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountEvents()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose() => _conn.Dispose();

    private List<PlaySessionRow> QueryPlays(DateTime? fromUtc, DateTime? toUtcExclusive, bool skippedOnly, int limit)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT track_key, title, artist, album, zone, source, started_utc,
                   listened_seconds, track_seconds, outcome, trigger
            FROM play_sessions
            WHERE source NOT IN ('linein', 'empty')
              AND IFNULL(title, '') != ''
              AND ($from = '' OR started_utc >= $from)
              AND ($to = '' OR started_utc < $to)
              AND ($skipped = 0 OR outcome = 'skipped')
            ORDER BY started_utc DESC, id DESC
            LIMIT $limit;
            """;
        BindRange(cmd, fromUtc, toUtcExclusive, skippedOnly, limit);
        return ReadSessions(cmd);
    }

    private List<RankedSongRow> Rank(DateTime? fromUtc, DateTime? toUtcExclusive, bool skipped, int limit)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = skipped
            ? """
              SELECT track_key,
                     COALESCE(NULLIF(MAX(title), ''), track_key) AS title,
                     COALESCE(MAX(artist), '') AS artist,
                     SUM(CASE WHEN outcome = 'skipped' THEN 1 ELSE 0 END) AS c
              FROM play_sessions
              WHERE source NOT IN ('linein', 'empty')
                AND IFNULL(title, '') != ''
                AND ($from = '' OR started_utc >= $from)
                AND ($to = '' OR started_utc < $to)
              GROUP BY track_key
              HAVING c > 0
              ORDER BY c DESC, title
              LIMIT $limit;
              """
            : """
              SELECT track_key,
                     COALESCE(NULLIF(MAX(title), ''), track_key) AS title,
                     COALESCE(MAX(artist), '') AS artist,
                     COUNT(*) AS c
              FROM play_sessions
              WHERE source NOT IN ('linein', 'empty')
                AND IFNULL(title, '') != ''
                AND ($from = '' OR started_utc >= $from)
                AND ($to = '' OR started_utc < $to)
              GROUP BY track_key
              ORDER BY c DESC, title
              LIMIT $limit;
              """;
        BindRange(cmd, fromUtc, toUtcExclusive, skippedOnly: false, limit);
        var rows = new List<RankedSongRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var count = Convert.ToInt32(reader.GetValue(3));
            var title = reader.GetString(1);
            var artist = reader.GetString(2);
            var who = string.IsNullOrWhiteSpace(artist) ? title : $"{title} — {artist}";
            rows.Add(new RankedSongRow
            {
                TrackKey = reader.GetString(0),
                Title = title,
                Artist = artist,
                Count = count,
                Label = $"{who}  ({count})",
            });
        }

        return rows;
    }

    private static void BindRange(SqliteCommand cmd, DateTime? fromUtc, DateTime? toUtcExclusive, bool skippedOnly, int limit)
    {
        cmd.Parameters.AddWithValue("$from", fromUtc is DateTime f ? Stamp(f) : "");
        cmd.Parameters.AddWithValue("$to", toUtcExclusive is DateTime t ? Stamp(t) : "");
        cmd.Parameters.AddWithValue("$skipped", skippedOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$limit", limit);
    }

    private static List<PlaySessionRow> ReadSessions(SqliteCommand cmd)
    {
        var rows = new List<PlaySessionRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var started = ParseUtc(reader.GetString(6)) ?? DateTime.UnixEpoch;
            var outcome = reader.IsDBNull(9) ? "" : reader.GetString(9);
            var trigger = reader.IsDBNull(10) ? "" : reader.GetString(10);
            double? listened = reader.IsDBNull(7) ? null : reader.GetDouble(7);
            double? length = reader.IsDBNull(8) ? null : reader.GetDouble(8);
            rows.Add(new PlaySessionRow
            {
                TrackKey = reader.GetString(0),
                Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Artist = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Album = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Zone = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Source = reader.IsDBNull(5) ? "" : reader.GetString(5),
                StartedUtc = started,
                WhenLocal = ActivityText.LocalStamp(started),
                Outcome = outcome,
                OutcomeLabel = ActivityText.OutcomeLabel(outcome),
                Trigger = trigger,
                TriggerLabel = ActivityText.TriggerLabel(trigger),
                Listened = ActivityText.FormatClock(listened),
                Length = ActivityText.FormatClock(length),
                ListenedSeconds = listened,
                TrackSeconds = length,
            });
        }

        return rows;
    }

    private void InsertSession(
        string trackKey, string? uri, string? title, string? artist, string? album, string? zone, string? source,
        DateTime startedUtc, DateTime endedUtc, double? listened, double? trackSeconds,
        string outcome, string trigger, bool seeded, SqliteTransaction tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO play_sessions(
              track_key, uri, title, artist, album, zone, source,
              started_utc, ended_utc, listened_seconds, track_seconds,
              outcome, trigger, seeded)
            VALUES (
              $key, $uri, $title, $artist, $album, $zone, $source,
              $started, $ended, $listened, $length,
              $outcome, $trigger, $seeded);
            """;
        cmd.Parameters.AddWithValue("$key", trackKey);
        cmd.Parameters.AddWithValue("$uri", (object?)uri ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artist", (object?)artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$album", (object?)album ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$zone", (object?)zone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)source ?? "other");
        cmd.Parameters.AddWithValue("$started", Stamp(startedUtc));
        cmd.Parameters.AddWithValue("$ended", Stamp(endedUtc));
        cmd.Parameters.AddWithValue("$listened", (object?)listened ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$length", (object?)trackSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$trigger", trigger);
        cmd.Parameters.AddWithValue("$seeded", seeded ? 1 : 0);
        cmd.ExecuteNonQuery();
        _writesSincePrune++;
    }

    private void InsertEvent(ActivityEventDraft draft, SqliteTransaction tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO events(
              utc, category, kind, summary, detail, zone, track_key, title, artist,
              outcome, trigger, listened_seconds, track_seconds)
            VALUES (
              $utc, $cat, $kind, $summary, $detail, $zone, $key, $title, $artist,
              $outcome, $trigger, $listened, $length);
            """;
        cmd.Parameters.AddWithValue("$utc", Stamp(draft.Utc));
        cmd.Parameters.AddWithValue("$cat", draft.Category);
        cmd.Parameters.AddWithValue("$kind", draft.Kind);
        cmd.Parameters.AddWithValue("$summary", draft.Summary);
        cmd.Parameters.AddWithValue("$detail", (object?)draft.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$zone", (object?)draft.Zone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", (object?)draft.TrackKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)draft.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artist", (object?)draft.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", (object?)draft.Outcome ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$trigger", (object?)draft.Trigger ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$listened", (object?)draft.ListenedSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$length", (object?)draft.TrackSeconds ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        _writesSincePrune++;
    }

    private void MaybePrune(bool force)
    {
        // Date cutoff is the heavier pass. The row cap is a no-op until the table
        // is actually over the limit, so it can run on every write.
        if (force || _writesSincePrune >= 40)
        {
            _writesSincePrune = 0;
            var cutoff = Stamp(DateTime.UtcNow.AddDays(-_retentionDays));
            Exec("DELETE FROM events WHERE utc < $c;", ("$c", cutoff));
            Exec("DELETE FROM play_sessions WHERE started_utc < $c;", ("$c", cutoff));
        }

        Exec(
            """
            DELETE FROM events WHERE id < (
              SELECT id FROM events ORDER BY id DESC LIMIT 1 OFFSET $keep
            );
            """,
            ("$keep", _maxEvents - 1));
        Exec(
            """
            DELETE FROM play_sessions WHERE id < (
              SELECT id FROM play_sessions ORDER BY id DESC LIMIT 1 OFFSET $keep
            );
            """,
            ("$keep", _maxSessions - 1));
    }

    private void EnsureSchema()
    {
        Exec(
            """
            CREATE TABLE IF NOT EXISTS events (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              utc TEXT NOT NULL,
              category TEXT NOT NULL,
              kind TEXT NOT NULL,
              summary TEXT NOT NULL,
              detail TEXT,
              zone TEXT,
              track_key TEXT,
              title TEXT,
              artist TEXT,
              outcome TEXT,
              trigger TEXT,
              listened_seconds REAL,
              track_seconds REAL
            );

            CREATE INDEX IF NOT EXISTS ix_events_utc ON events(utc DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_events_cat ON events(category, utc DESC);

            CREATE TABLE IF NOT EXISTS play_sessions (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              track_key TEXT NOT NULL,
              uri TEXT,
              title TEXT,
              artist TEXT,
              album TEXT,
              zone TEXT,
              source TEXT,
              started_utc TEXT NOT NULL,
              ended_utc TEXT,
              listened_seconds REAL,
              track_seconds REAL,
              outcome TEXT NOT NULL,
              trigger TEXT,
              seeded INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_started ON play_sessions(started_utc DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_sessions_key ON play_sessions(track_key, started_utc DESC);

            CREATE TABLE IF NOT EXISTS meta (
              key TEXT NOT NULL PRIMARY KEY,
              value TEXT
            );
            """);
    }

    private void Exec(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    internal static string Stamp(DateTime utc) =>
        DateTime.SpecifyKind(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

    private static DateTime? ParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return null;
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
