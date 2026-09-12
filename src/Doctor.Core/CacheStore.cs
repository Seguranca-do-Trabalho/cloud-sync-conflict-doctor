namespace Doctor.Core;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

/// <summary>
/// Cache integrity exception (threat-model T-08, rule R8; card T19):
/// thrown when the cache database presents an integrity violation — row
/// whose MAC does not match, tampered schema, <c>schema_version</c>
/// incompatible or corrupted SQLite structure. Fail-closed: corruption is
/// DISTINGUISHED from absence (miss) and never becomes silent recalculation nor
/// returns poisoned hash to the pipeline.
/// </summary>
public sealed class CacheCorruptedException : InvalidOperationException
{
    public CacheCorruptedException(string message)
        : base($"cache corrupted (fail-closed): {message}")
        => Reason = message;

    public CacheCorruptedException(string message, Exception innerException)
        : base($"cache corrupted (fail-closed): {message}", innerException)
        => Reason = message;

    /// <summary>Technical reason for rejection (audit; no content data).</summary>
    public string Reason { get; }
}

/// <summary>
/// Incremental SQLite cache (SPEC §12; ADR-0006; cards T11 t_c7004baf + T19
/// t_43803664 — identity and fail-closed, consolidating S11-3/S11-4).
///
/// Schema v2 (T19), key = BLAKE3(path,size,mtime) — NEVER absolute path:
///   cache_hashes(key_hex TEXT PK, size INTEGER, mtime_ticks INTEGER,
///                hash_partial TEXT, hash_full TEXT, algorithm TEXT NOT NULL,
///                hash_version INTEGER NOT NULL, entry_mac TEXT NOT NULL,
///                last_seen TEXT)
///   cache_meta(key TEXT PK, value TEXT NOT NULL)  -- schema_version etc.
///
/// Identity (T-08/R8): the logical key is BLAKE3 hash of the canonical tuple
/// (normalized path, size, mtime ticks) with domain prefix
/// "ccd-cache-key-v1" (recipe pinned by test P1). The path is never
/// persisted — rename/move changes the key ⇒ conservative recalculation; same
/// state ⇒ same key (determinism §3). Row integrity: entry_mac
/// = BLAKE3("ccd-cache-mac-v1" || key || algorithm || hash_version ||
/// hash_full/partial) written on Upsert and REVALIDATED on every read — row
/// tampered on disk ⇒ <see cref="CacheCorruptedException"/> (fail-closed;
/// poison is never served, corruption ≠ miss).
///
/// Fail-closed opening: invalid SQLite header, missing/tampered schema or
/// incompatible schema_version ⇒ exception on opening. The user's database
/// is NEVER silently recreated/deleted.
///
/// Microsoft.Data.Sqlite PINNED (8.0.8) via Directory.Packages.props (ADR-0001/0006,
/// risk R9). WAL; Pooling=false ⇒ real single process connection (exclusive access,
/// no server, zero network — §26). Default database at %LOCALAPPDATA%/ConflictDoctor/cache.db;
/// INJECTABLE path via constructor for tests (tmp).
///
/// Security (threat model T-08): TryGetByKey only returns hash with size AND mtime
/// matching — mtime with CONSERVATIVE 2 s tolerance: WHEN IN DOUBT, RECALCULATE.
/// Divergence in any key column ⇒ miss ⇒ mandatory recalculation; poisoned cache
/// never masks the disk. Presence/absence of hit never changes the
/// emitted report (determinism §3).
/// </summary>
public sealed class CacheStore : IDisposable
{
    private const string DefaultRelativePath = "ConflictDoctor/cache.db";

    /// <summary>Conservative mtime tolerance (FAT/NTFS rounding): when in doubt, recalculate.</summary>
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>v2 schema version managed by this code (cache_meta.schema_version).</summary>
    internal const int SchemaVersion = 2;

    /// <summary>v1 recipe key domain prefix — pinned by test P1.</summary>
    internal const string KeyDomainTag = "ccd-cache-key-v1";

    /// <summary>Per-row MAC domain prefix (T-08 integrity).</summary>
    private const string MacDomainTag = "ccd-cache-mac-v1";

    /// <summary>Algorithm/hash version fixed by the v1 recipe (ADR-0005/0006).</summary>
    private const string AlgorithmV1 = "BLAKE3";
    private const int HashVersionV1 = 1;

    private readonly SqliteConnection _connection;

    /// <summary>
    /// Opens the cache at the given path (injectable for tests). null uses the product default:
    /// %LOCALAPPDATA%/ConflictDoctor/cache.db (Unix: $XDG_DATA_HOME or ~/.local/share).
    ///
    /// Fail-closed (T19): existing database with unreadable header, corrupted structure
    /// or legacy/incompatible v1 schema throws <see cref="CacheCorruptedException"/>
    /// (legacy v1 has no entry_mac or cache_meta — not verifiable ⇒ not opened).
    /// </summary>
    public CacheStore(string? databasePath = null)
    {
        var path = databasePath ?? ResolveDefaultDatabasePath();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // single-process exclusive access: one real connection, no pool
        }.ToString());

        try
        {
            _connection.Open();

            Execute("PRAGMA journal_mode=WAL");
            Execute("PRAGMA synchronous=FULL");

            OpenOrThrow();
        }
        catch (CacheCorruptedException)
        {
            _connection.Dispose();
            throw;
        }
        catch (SqliteException ex)
        {
            // Corrupted header/pages throw here on first read:
            // named fail-closed, without recycling the user's database.
            _connection.Dispose();
            throw new CacheCorruptedException($"invalid SQLite structure ({ex.SqliteErrorCode}: {ex.Message})", ex);
        }
    }

    /// <summary>
    /// Ensures v2 schema on a NEW database or validates structural integrity of an
    /// EXISTING database (card T19): expected columns present, cache_meta.schema_version
    /// compatible and structural check clean. Any deviation ⇒
    /// <see cref="CacheCorruptedException"/> — the database is never recreated over it.
    /// </summary>
    private void OpenOrThrow()
    {
        var tableExists = (long)(QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cache_hashes'") ?? 0L) == 1L;

        if (!tableExists)
        {
            var anyUserTable = (long)(QueryScalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'") ?? 0L) > 0L;
            if (anyUserTable)
            {
                throw new CacheCorruptedException(
                    "database without cache_hashes but with other tables — unknown schema");
            }

            CreateSchema();
            return;
        }

        ValidateExistingSchema();
    }

    /// <summary>Creates complete v2 schema (table + meta) in a new database.</summary>
    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS cache_hashes (
                key_hex      TEXT NOT NULL PRIMARY KEY, -- BLAKE3 hex of (path,size,mtime) — never the path
                size         INTEGER NOT NULL,          -- bytes; divergence invalidates
                mtime_ticks  INTEGER NOT NULL,          -- DateTime.Ticks UTC; 2 s tolerance on read
                hash_partial TEXT,                      -- BLAKE3 windows v1 (ADR-0005 §3)
                hash_full    TEXT,                      -- only genuine complete (≤128 KiB or L3)
                algorithm    TEXT NOT NULL,             -- 'BLAKE3' fixed in v1 recipe
                hash_version INTEGER NOT NULL,          -- 1
                entry_mac    TEXT NOT NULL,             -- BLAKE3 of protected columns (T-08 integrity)
                last_seen    TEXT NOT NULL              -- ISO-8601 UTC of last write (pruning)
            )
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS cache_meta (
                key   TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            )
            """);
        UpsertMeta("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Structural validation of existing database (fail-closed): full columns,
    /// schema_version present and compatible, structural check clean.
    /// </summary>
    private void ValidateExistingSchema()
    {
        // 1. Expected columns — tampered schema (integrity column removed,
        //    types swapped) is not opened.
        var expectedColumns = new[]
        {
            "key_hex", "size", "mtime_ticks", "hash_partial",
            "hash_full", "algorithm", "hash_version", "entry_mac", "last_seen",
        };
        var present = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(cache_hashes)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                present.Add(reader.GetString(1));
            }
        }

        var missing = expectedColumns.Where(c => !present.Contains(c)).ToArray();
        if (missing.Length > 0)
        {
            throw new CacheCorruptedException(
                $"missing/tampered columns in cache_hashes: {string.Join(", ", missing)}");
        }

        // 2. cache_meta present and schema_version compatible.
        var metaExists = (long)(QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cache_meta'") ?? 0L) == 1L;
        if (!metaExists)
        {
            throw new CacheCorruptedException("cache_meta missing — incompatible schema");
        }

        var storedVersion = QueryScalar(
            "SELECT value FROM cache_meta WHERE key = 'schema_version'") as string;
        if (storedVersion is null
            || !int.TryParse(storedVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            || version != SchemaVersion)
        {
            throw new CacheCorruptedException(
                $"schema_version '{storedVersion ?? "(missing)"}' incompatible with {SchemaVersion}");
        }

        // 3. Deep structural check: corrupted pages (bit flip on disk)
        //    are detected HERE, at opening — never later, "because it
        //    seems to work".
        var quick = QueryScalar("PRAGMA quick_check(1)") as string;
        if (!string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new CacheCorruptedException($"SQLite structural check: {quick ?? "(no response)"}");
        }
    }

    public static string ResolveDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            localAppData = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(localAppData, DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// v1 logical key (card T19; pinned by test P1): BLAKE3 of the canonical flow
    /// <c>"ccd-cache-key-v1" || UTF-8(path) || size int64 BE || mtime.Ticks int64 BE</c>,
    /// in lowercase hex of 64 characters. The path participates ONLY in the derivation —
    /// it is never persisted (proof P3). Same state ⇒ same key (§3).
    /// </summary>
    public static string ComputeKey(string path, long size, DateTime mtimeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var normalized = Path.GetFullPath(path);
        var ticks = mtimeUtc.ToUniversalTime().Ticks;

        using var hasher = Blake3.Hasher.New();
        hasher.Update(Encoding.ASCII.GetBytes(KeyDomainTag));
        hasher.Update(Encoding.UTF8.GetBytes(normalized));
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, size);
        hasher.Update(buf);
        BinaryPrimitives.WriteInt64BigEndian(buf, ticks);
        hasher.Update(buf);

        return Convert.ToHexString(hasher.Finalize().AsSpan()).ToLowerInvariant();
    }

    /// <summary>
    /// Per-row MAC (T-08/R8 integrity): BLAKE3 of
    /// <c>"ccd-cache-mac-v1" || key_hex || algorithm || hash_version ||
    /// size || mtime_ticks || hash_full || hash_partial</c> with '\n' separators.
    /// Written on Upsert and revalidated on EVERY read against the values READ from
    /// disk: any decision column tampered outside the API (hash, size,
    /// mtime, algorithm, hash_version) breaks the MAC ⇒
    /// <see cref="CacheCorruptedException"/> — poison is never served.
    /// </summary>
    private static string ComputeEntryMac(
        string keyHex, string algorithm, int hashVersion, long size, long mtimeTicks,
        string? hashFull, string? hashPartial)
    {
        using var hasher = Blake3.Hasher.New();
        hasher.Update(Encoding.ASCII.GetBytes(MacDomainTag));
        var fields = new[]
        {
            keyHex,
            algorithm,
            hashVersion.ToString(CultureInfo.InvariantCulture),
            size.ToString(CultureInfo.InvariantCulture),
            mtimeTicks.ToString(CultureInfo.InvariantCulture),
            hashFull ?? string.Empty,
            hashPartial ?? string.Empty,
        };
        foreach (var field in fields)
        {
            hasher.Update(new[] { (byte)'\n' });
            hasher.Update(Encoding.UTF8.GetBytes(field));
        }

        return Convert.ToHexString(hasher.Finalize().AsSpan()).ToLowerInvariant();
    }

    /// <summary>
    /// Writes (or replaces) the entry derived from (path,size,mtime). The path
    /// is NOT persisted — only the BLAKE3 key (proof P3). The MAC covers all
    /// decision columns: any edit outside the API invalidates the row.
    /// </summary>
    public void UpsertByPath(string path, long size, DateTime mtimeUtc, string? hashFull, string? hashPartial = null)
    {
        var keyHex = ComputeKey(path, size, mtimeUtc);
        var mtimeTicks = mtimeUtc.ToUniversalTime().Ticks;
        var mac = ComputeEntryMac(keyHex, AlgorithmV1, HashVersionV1, size, mtimeTicks, hashFull, hashPartial);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO cache_hashes
                (key_hex, size, mtime_ticks, hash_partial, hash_full, algorithm, hash_version, entry_mac, last_seen)
            VALUES ($key, $size, $mtime, $hp, $hf, $alg, $hv, $mac, $seen)
            ON CONFLICT(key_hex) DO UPDATE SET
                size = excluded.size,
                mtime_ticks = excluded.mtime_ticks,
                hash_partial = excluded.hash_partial,
                hash_full = excluded.hash_full,
                algorithm = excluded.algorithm,
                hash_version = excluded.hash_version,
                entry_mac = excluded.entry_mac,
                last_seen = excluded.last_seen
            """;
        cmd.Parameters.AddWithValue("$key", keyHex);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$mtime", mtimeTicks);
        cmd.Parameters.AddWithValue("$hp", (object?)hashPartial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hf", (object?)hashFull ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$alg", AlgorithmV1);
        cmd.Parameters.AddWithValue("$hv", HashVersionV1);
        cmd.Parameters.AddWithValue("$mac", mac);
        cmd.Parameters.AddWithValue("$seen", ToIso(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Cached hash with EXACT match only of the key derived from
    /// (path,size,mtime ≤ 2 s). Integrity first (T-08): row present with
    /// divergent entry_mac ⇒ <see cref="CacheCorruptedException"/> (fail-closed;
    /// corruption is DISTINCT from miss). Missing row or divergent metadata ⇒
    /// null ⇒ recalculation. Never promotes partial to complete.
    /// </summary>
    public string? TryGetByKey(string path, long size, DateTime mtimeUtc)
    {
        var keyHex = ComputeKey(path, size, mtimeUtc);

        string? storedMac;
        string? storedAlgorithm;
        int storedHashVersion;
        string? storedHashFull;
        string? storedHashPartial;
        long storedSize;
        long storedMtime;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT size, mtime_ticks, hash_partial, hash_full, algorithm, hash_version, entry_mac
                  FROM cache_hashes WHERE key_hex = $key
                """;
            cmd.Parameters.AddWithValue("$key", keyHex);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null; // missing ⇒ miss ⇒ recalculation (calm behavior)
            }

            storedSize = reader.GetInt64(0);
            storedMtime = reader.GetInt64(1);
            storedHashPartial = reader.IsDBNull(2) ? null : reader.GetString(2);
            storedHashFull = reader.IsDBNull(3) ? null : reader.GetString(3);
            storedAlgorithm = reader.GetString(4);
            storedHashVersion = reader.GetInt64(5) switch { _ => checked((int)reader.GetInt64(5)) };
            storedMac = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        // Per-row integrity BEFORE any use of the value (fail-closed).
        // Recipe guards (R8): version 1 and algorithm BLAKE3 — forced downgrade
        // at column-level is rejected by MAC, but the recipe guard
        // ensures no row outside v1 recipe is accepted.
        if (storedAlgorithm != AlgorithmV1 || storedHashVersion != HashVersionV1)
        {
            throw new CacheCorruptedException(
                $"invalid v1 recipe in row {keyHex[..12]}… (algorithm={storedAlgorithm}, hash_version={storedHashVersion} — expected {AlgorithmV1}/{HashVersionV1})");
        }

        var expectedMac = ComputeEntryMac(keyHex, storedAlgorithm, storedHashVersion, storedSize, storedMtime, storedHashFull, storedHashPartial);
        if (storedMac is null || !FixedTimeEquals(storedMac, expectedMac))
        {
            throw new CacheCorruptedException($"divergent entry_mac in row {keyHex[..12]}… — content tampered");
        }

        // Divergent metadata ⇒ miss ⇒ recalculation (never exception).
        if (storedSize != size
            || (TimeSpan.FromTicks(storedMtime - mtimeUtc.ToUniversalTime().Ticks)).Duration() > MtimeTolerance)
        {
            return null;
        }

        return storedHashFull;
    }

    /// <summary>Byte-by-byte comparison without short-circuit (hygiene, not secret).</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) { return false; }
        var diff = 0;
        for (var i = 0; i < a.Length; i++) { diff |= a[i] ^ b[i]; }
        return diff == 0;
    }

    /// <summary>Escape hatch for tests: direct SQL (fixtures simulate external tampering).</summary>
    internal SqliteCommand CreateCommandForTests() => _connection.CreateCommand();

    private void UpsertMeta(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO cache_meta (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    internal object? QueryScalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string ToIso(DateTime value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
