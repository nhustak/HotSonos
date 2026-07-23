using System.IO;
using Microsoft.Data.Sqlite;

namespace HotSonos.App.Library;

/// <summary>
/// Rebuildable SQLite cache under %LocalAppData%\HotSonos. Never the sole tag store.
/// </summary>
public sealed class LibraryDb : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private SqliteConnection? _conn;

    public LibraryDb(string? databasePath = null)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotSonos");
        Directory.CreateDirectory(dir);
        _path = databasePath ?? System.IO.Path.Combine(dir, "library.db");
    }

    public string DatabasePath => _path;

    public void Open()
    {
        lock (_gate)
        {
            if (_conn is not null)
                return;

            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString());
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                cmd.ExecuteNonQuery();
            }

            EnsureSchema(conn);
            _conn = conn;
        }
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        // Base table (IF NOT EXISTS leaves older DBs unchanged — columns migrated below).
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS tracks (
                  path TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                  root TEXT NOT NULL,
                  relative_path TEXT,
                  title TEXT,
                  artist TEXT,
                  album TEXT,
                  album_artist TEXT,
                  genre TEXT,
                  track_number INTEGER,
                  year INTEGER,
                  duration_ms INTEGER,
                  tempo TEXT,
                  bpm REAL,
                  file_size INTEGER NOT NULL,
                  file_mtime_utc TEXT NOT NULL,
                  last_scanned_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS meta (
                  key TEXT NOT NULL PRIMARY KEY,
                  value TEXT
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // Migrate older DBs before creating indexes that reference new columns.
        EnsureColumn(conn, "codec", "TEXT");
        EnsureColumn(conn, "sample_rate_hz", "INTEGER");
        EnsureColumn(conn, "bits_per_sample", "INTEGER");
        EnsureColumn(conn, "channels", "INTEGER");
        EnsureColumn(conn, "bitrate_kbps", "INTEGER");
        EnsureColumn(conn, "sonos_playable", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(conn, "sonos_play_issue", "TEXT");
        // Manual / auto-linked twin under MasterLibraryRoot (not overwritten by rescan upsert).
        EnsureColumn(conn, "master_path", "TEXT");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE INDEX IF NOT EXISTS ix_tracks_artist ON tracks(artist);
                CREATE INDEX IF NOT EXISTS ix_tracks_album ON tracks(album);
                CREATE INDEX IF NOT EXISTS ix_tracks_title ON tracks(title);
                CREATE INDEX IF NOT EXISTS ix_tracks_tempo ON tracks(tempo);
                CREATE INDEX IF NOT EXISTS ix_tracks_root ON tracks(root);
                CREATE INDEX IF NOT EXISTS ix_tracks_sonos_playable ON tracks(sonos_playable);
                CREATE INDEX IF NOT EXISTS ix_tracks_master_path ON tracks(master_path);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private static void EnsureColumn(SqliteConnection conn, string name, string typeSql)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var list = conn.CreateCommand())
        {
            list.CommandText = "PRAGMA table_info(tracks);";
            using var reader = list.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1)); // name column
        }

        if (existing.Contains(name))
            return;

        using var alter = conn.CreateCommand();
        // SQLite: NOT NULL on ADD COLUMN requires a DEFAULT.
        alter.CommandText = $"ALTER TABLE tracks ADD COLUMN {name} {typeSql};";
        alter.ExecuteNonQuery();
    }

    public int CountTracks()
    {
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tracks;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int CountSonosUnplayable()
    {
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tracks WHERE sonos_playable = 0;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>True when any row is missing audio technical fields (needs force re-scan).</summary>
    public bool HasTracksMissingAudioProps()
    {
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                """
                SELECT COUNT(*) FROM tracks
                WHERE sample_rate_hz IS NULL AND bits_per_sample IS NULL AND bitrate_kbps IS NULL AND codec IS NULL;
                """;
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string? value)
    {
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO meta(key, value) VALUES ($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Returns known (path → size, mtimeUtc) for skip-if-unchanged.</summary>
    public Dictionary<string, (long Size, DateTime MtimeUtc)> LoadFingerprints(IEnumerable<string> roots)
    {
        var rootList = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var map = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        if (rootList.Count == 0)
            return map;

        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            // Load all fingerprints; filter by root in memory if needed (roots few).
            cmd.CommandText = "SELECT path, file_size, file_mtime_utc, root FROM tracks;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var root = reader.GetString(3);
                if (!rootList.Any(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var path = reader.GetString(0);
                var size = reader.GetInt64(1);
                var mtime = DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind);
                map[path] = (size, mtime);
            }
        }

        return map;
    }

    public void UpsertTracks(IReadOnlyList<LibraryTrack> tracks)
    {
        if (tracks.Count == 0)
            return;

        lock (_gate)
        {
            EnsureOpen();
            using var tx = _conn!.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO tracks (
                  path, root, relative_path, title, artist, album, album_artist, genre,
                  track_number, year, duration_ms, tempo, bpm,
                  codec, sample_rate_hz, bits_per_sample, channels, bitrate_kbps,
                  sonos_playable, sonos_play_issue,
                  file_size, file_mtime_utc, last_scanned_utc)
                VALUES (
                  $path, $root, $rel, $title, $artist, $album, $albumArtist, $genre,
                  $track, $year, $dur, $tempo, $bpm,
                  $codec, $sr, $bits, $ch, $br,
                  $playable, $issue,
                  $size, $mtime, $scanned)
                ON CONFLICT(path) DO UPDATE SET
                  root = excluded.root,
                  relative_path = excluded.relative_path,
                  title = excluded.title,
                  artist = excluded.artist,
                  album = excluded.album,
                  album_artist = excluded.album_artist,
                  genre = excluded.genre,
                  track_number = excluded.track_number,
                  year = excluded.year,
                  duration_ms = excluded.duration_ms,
                  tempo = excluded.tempo,
                  bpm = excluded.bpm,
                  codec = excluded.codec,
                  sample_rate_hz = excluded.sample_rate_hz,
                  bits_per_sample = excluded.bits_per_sample,
                  channels = excluded.channels,
                  bitrate_kbps = excluded.bitrate_kbps,
                  sonos_playable = excluded.sonos_playable,
                  sonos_play_issue = excluded.sonos_play_issue,
                  file_size = excluded.file_size,
                  file_mtime_utc = excluded.file_mtime_utc,
                  last_scanned_utc = excluded.last_scanned_utc;
                """;

            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pRoot = cmd.Parameters.Add("$root", SqliteType.Text);
            var pRel = cmd.Parameters.Add("$rel", SqliteType.Text);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pArtist = cmd.Parameters.Add("$artist", SqliteType.Text);
            var pAlbum = cmd.Parameters.Add("$album", SqliteType.Text);
            var pAlbumArtist = cmd.Parameters.Add("$albumArtist", SqliteType.Text);
            var pGenre = cmd.Parameters.Add("$genre", SqliteType.Text);
            var pTrack = cmd.Parameters.Add("$track", SqliteType.Integer);
            var pYear = cmd.Parameters.Add("$year", SqliteType.Integer);
            var pDur = cmd.Parameters.Add("$dur", SqliteType.Integer);
            var pTempo = cmd.Parameters.Add("$tempo", SqliteType.Text);
            var pBpm = cmd.Parameters.Add("$bpm", SqliteType.Real);
            var pCodec = cmd.Parameters.Add("$codec", SqliteType.Text);
            var pSr = cmd.Parameters.Add("$sr", SqliteType.Integer);
            var pBits = cmd.Parameters.Add("$bits", SqliteType.Integer);
            var pCh = cmd.Parameters.Add("$ch", SqliteType.Integer);
            var pBr = cmd.Parameters.Add("$br", SqliteType.Integer);
            var pPlayable = cmd.Parameters.Add("$playable", SqliteType.Integer);
            var pIssue = cmd.Parameters.Add("$issue", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pMtime = cmd.Parameters.Add("$mtime", SqliteType.Text);
            var pScanned = cmd.Parameters.Add("$scanned", SqliteType.Text);

            foreach (var t in tracks)
            {
                pPath.Value = t.Path;
                pRoot.Value = t.Root;
                pRel.Value = (object?)t.RelativePath ?? DBNull.Value;
                pTitle.Value = (object?)t.Title ?? DBNull.Value;
                pArtist.Value = (object?)t.Artist ?? DBNull.Value;
                pAlbum.Value = (object?)t.Album ?? DBNull.Value;
                pAlbumArtist.Value = (object?)t.AlbumArtist ?? DBNull.Value;
                pGenre.Value = (object?)t.Genre ?? DBNull.Value;
                pTrack.Value = (object?)t.TrackNumber ?? DBNull.Value;
                pYear.Value = (object?)t.Year ?? DBNull.Value;
                pDur.Value = (object?)t.DurationMs ?? DBNull.Value;
                pTempo.Value = (object?)t.Tempo ?? DBNull.Value;
                pBpm.Value = (object?)t.Bpm ?? DBNull.Value;
                pCodec.Value = (object?)t.Codec ?? DBNull.Value;
                pSr.Value = (object?)t.SampleRateHz ?? DBNull.Value;
                pBits.Value = (object?)t.BitsPerSample ?? DBNull.Value;
                pCh.Value = (object?)t.Channels ?? DBNull.Value;
                pBr.Value = (object?)t.BitrateKbps ?? DBNull.Value;
                pPlayable.Value = t.SonosPlayable ? 1 : 0;
                pIssue.Value = (object?)t.SonosPlayIssue ?? DBNull.Value;
                pSize.Value = t.FileSize;
                pMtime.Value = t.FileMtimeUtc.ToString("o");
                pScanned.Value = t.LastScannedUtc.ToString("o");
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Deletes rows under <paramref name="roots"/> whose path is not in <paramref name="keepPaths"/>.</summary>
    public int DeleteMissing(IReadOnlyList<string> roots, HashSet<string> keepPaths)
    {
        lock (_gate)
        {
            EnsureOpen();
            var toDelete = new List<string>();
            using (var cmd = _conn!.CreateCommand())
            {
                cmd.CommandText = "SELECT path, root FROM tracks;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    var root = reader.GetString(1);
                    if (!roots.Any(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!keepPaths.Contains(path))
                        toDelete.Add(path);
                }
            }

            if (toDelete.Count == 0)
                return 0;

            using var tx = _conn.BeginTransaction();
            using var del = _conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tracks WHERE path = $p;";
            var p = del.Parameters.Add("$p", SqliteType.Text);
            foreach (var path in toDelete)
            {
                p.Value = path;
                del.ExecuteNonQuery();
            }

            tx.Commit();
            return toDelete.Count;
        }
    }

    private const string SelectTrackCols =
        """
        path, root, relative_path, title, artist, album, album_artist, genre,
        track_number, year, duration_ms, tempo, bpm,
        codec, sample_rate_hz, bits_per_sample, channels, bitrate_kbps,
        sonos_playable, sonos_play_issue,
        file_size, file_mtime_utc, last_scanned_utc,
        master_path
        """;

    public IReadOnlyList<LibraryTrack> Search(string? query, int limit, int offset, bool sonosUnplayableOnly = false)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        var q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            var playFilter = sonosUnplayableOnly ? "sonos_playable = 0" : "1=1";
            if (q is null)
            {
                cmd.CommandText =
                    $"""
                    SELECT {SelectTrackCols}
                    FROM tracks
                    WHERE {playFilter}
                    ORDER BY artist, album, track_number, title
                    LIMIT $lim OFFSET $off;
                    """;
            }
            else
            {
                cmd.CommandText =
                    $"""
                    SELECT {SelectTrackCols}
                    FROM tracks
                    WHERE {playFilter}
                      AND (title LIKE $q ESCAPE '\'
                       OR artist LIKE $q ESCAPE '\'
                       OR album LIKE $q ESCAPE '\'
                       OR genre LIKE $q ESCAPE '\'
                       OR tempo LIKE $q ESCAPE '\'
                       OR codec LIKE $q ESCAPE '\'
                       OR sonos_play_issue LIKE $q ESCAPE '\'
                       OR path LIKE $q ESCAPE '\')
                    ORDER BY artist, album, track_number, title
                    LIMIT $lim OFFSET $off;
                    """;
                var escaped = EscapeLike(q);
                cmd.Parameters.AddWithValue("$q", $"%{escaped}%");
            }

            cmd.Parameters.AddWithValue("$lim", limit);
            cmd.Parameters.AddWithValue("$off", offset);

            var list = new List<LibraryTrack>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(ReadTrack(reader));
            return list;
        }
    }

    public LibraryTrack? GetByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT {SelectTrackCols}
                FROM tracks WHERE path = $p COLLATE NOCASE LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$p", path.Trim());
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTrack(reader) : null;
        }
    }

    /// <summary>Match a track by Sonos x-file-cifs URI or UNC path suffix.</summary>
    public LibraryTrack? FindBySonosUriOrUnc(string? uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath))
            return null;

        if (SonosPath.TryToUnc(uriOrPath, out var unc))
        {
            var byPath = GetByPath(unc);
            if (byPath is not null)
                return byPath;
        }

        // Fallback: match filename + parent folder via LIKE on path.
        string? fileName = null;
        try
        {
            var raw = SonosPath.TryToUnc(uriOrPath, out var u)
                ? u
                : uriOrPath.Replace('/', '\\');
            fileName = System.IO.Path.GetFileName(raw);
        }
        catch { /* ignore */ }

        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT {SelectTrackCols}
                FROM tracks WHERE path LIKE $p ESCAPE '\'
                ORDER BY length(path) ASC LIMIT 5;
                """;
            cmd.Parameters.AddWithValue("$p", "%\\" + EscapeLike(fileName));
            using var reader = cmd.ExecuteReader();
            LibraryTrack? first = null;
            var count = 0;
            while (reader.Read())
            {
                count++;
                first ??= ReadTrack(reader);
            }
            // Only accept unambiguous filename match.
            return count == 1 ? first : null;
        }
    }

    private static LibraryTrack ReadTrack(SqliteDataReader reader) => new()
    {
        Path = reader.GetString(0),
        Root = reader.GetString(1),
        RelativePath = reader.IsDBNull(2) ? null : reader.GetString(2),
        Title = reader.IsDBNull(3) ? null : reader.GetString(3),
        Artist = reader.IsDBNull(4) ? null : reader.GetString(4),
        Album = reader.IsDBNull(5) ? null : reader.GetString(5),
        AlbumArtist = reader.IsDBNull(6) ? null : reader.GetString(6),
        Genre = reader.IsDBNull(7) ? null : reader.GetString(7),
        TrackNumber = reader.IsDBNull(8) ? null : reader.GetInt32(8),
        Year = reader.IsDBNull(9) ? null : reader.GetInt32(9),
        DurationMs = reader.IsDBNull(10) ? null : reader.GetInt64(10),
        Tempo = reader.IsDBNull(11) ? null : reader.GetString(11),
        Bpm = reader.IsDBNull(12) ? null : reader.GetDouble(12),
        Codec = reader.IsDBNull(13) ? null : reader.GetString(13),
        SampleRateHz = reader.IsDBNull(14) ? null : reader.GetInt32(14),
        BitsPerSample = reader.IsDBNull(15) ? null : reader.GetInt32(15),
        Channels = reader.IsDBNull(16) ? null : reader.GetInt32(16),
        BitrateKbps = reader.IsDBNull(17) ? null : reader.GetInt32(17),
        SonosPlayable = !reader.IsDBNull(18) && reader.GetInt32(18) != 0,
        SonosPlayIssue = reader.IsDBNull(19) ? null : reader.GetString(19),
        FileSize = reader.GetInt64(20),
        FileMtimeUtc = DateTime.Parse(reader.GetString(21), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LastScannedUtc = DateTime.Parse(reader.GetString(22), null, System.Globalization.DateTimeStyles.RoundtripKind),
        MasterPath = reader.FieldCount > 23 && !reader.IsDBNull(23) ? reader.GetString(23) : null,
    };

    /// <summary>
    /// Persist or clear a manual/auto master twin link. Does not write tags.
    /// Returns false if the Sonos track path is not in the cache.
    /// </summary>
    public bool SetMasterPath(string sonosPath, string? masterPath)
    {
        if (string.IsNullOrWhiteSpace(sonosPath))
            return false;

        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET master_path = $m
                WHERE path = $p COLLATE NOCASE;
                """;
            cmd.Parameters.AddWithValue("$p", sonosPath.Trim());
            cmd.Parameters.AddWithValue("$m",
                string.IsNullOrWhiteSpace(masterPath) ? DBNull.Value : masterPath.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    private void EnsureOpen()
    {
        if (_conn is null)
            Open();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _conn?.Dispose();
            _conn = null;
        }
    }
}
