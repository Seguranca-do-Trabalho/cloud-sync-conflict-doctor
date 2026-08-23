namespace Doctor.Core;

using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>
/// Cache incremental SQLite (SPEC §12; ADR-0006; card T11 t_c7004baf — escopo reduzido
/// do orquestrador: SOMENTE a tabela de cache, sem integração no pipeline).
///
/// Schema v1 do orquestrador, EXATO:
///   cache_hashes(file_id INTEGER PK, size INTEGER, mtime_utc TEXT, hash_partial TEXT,
///                hash_full TEXT, algorithm TEXT DEFAULT 'BLAKE3',
///                hash_version INTEGER DEFAULT 1, last_seen TEXT)
///
/// Microsoft.Data.Sqlite PINADO (8.0.8) via Directory.Packages.props (ADR-0001/0006,
/// risco R9). WAL; Pooling=false ⇒ conexão única real do processo (acesso exclusivo,
/// sem servidor, zero rede — §26). Banco padrão em %LOCALAPPDATA%/ConflictDoctor/cache.db;
/// caminho INJETÁVEL pelo construtor para testes (tmp).
///
/// Segurança (threat model T-08): TryGet só devolve hash_full com size E mtime batendo
/// — mtime com tolerância CONSERVADORA de 2 s: NA DÚVIDA, RECALCULA. Divergência de
/// qualquer coluna-chave ⇒ null ⇒ recálculo obrigatório; cache venenoso nunca mascara
/// o disco. Presença/ausência de hit jamais muda o relatório emitido (determinismo §3).
/// </summary>
public sealed class CacheStore : IDisposable
{
    private const string DefaultRelativePath = "ConflictDoctor/cache.db";

    /// <summary>Tolerância conservadora de mtime (rounding FAT/NTFS): na dúvida, recalcula.</summary>
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    private readonly SqliteConnection _connection;

    /// <summary>
    /// Abre o cache no caminho dado (injetável para testes). null usa o padrão do produto:
    /// %LOCALAPPDATA%/ConflictDoctor/cache.db (Unix: $XDG_DATA_HOME ou ~/.local/share).
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
            Pooling = false, // acesso exclusivo do processo: uma conexão real, sem pool
        }.ToString());

        _connection.Open();

        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=FULL");
        EnsureTable(); // schema garantido na abertura: TryGet/Upsert válidos imediatamente
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
    /// Cria a tabela cache_hashes se ausente. Idempotente e chamada na abertura;
    /// reexecutar é seguro. Retorna true quando a tabela JÁ existia, false se criada agora.
    /// </summary>
    public bool EnsureTable()
    {
        var alreadyExisted = (long)(QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cache_hashes'") ?? 0L) == 1L;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS cache_hashes (
                file_id       INTEGER NOT NULL PRIMARY KEY,  -- NTFS file ID / inode: renome/move preservam
                size          INTEGER NOT NULL,              -- bytes; divergência invalida
                mtime_utc     TEXT NOT NULL,                 -- ISO-8601 UTC; divergência > 2 s invalida
                hash_partial  TEXT,                          -- BLAKE3 janelas v1 (ADR-0005 §3)
                hash_full     TEXT,                          -- só completo genuíno (≤128 KiB ou L3)
                algorithm     TEXT NOT NULL DEFAULT 'BLAKE3',
                hash_version  INTEGER NOT NULL DEFAULT 1,
                last_seen     TEXT NOT NULL                  -- ISO-8601 UTC da última gravação (poda)
            )
            """;
        cmd.ExecuteNonQuery();
        return alreadyExisted;
    }

    /// <summary>
    /// hash_full em cache somente com correspondência EXATA de (file_id, size, mtime ≤ 2 s):
    /// qualquer divergência devolve null ⇒ recálculo. Nunca promove parcial a completo.
    /// </summary>
    public string? TryGet(long fileId, long size, DateTime mtimeUtc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT hash_full, mtime_utc FROM cache_hashes
             WHERE file_id = $fid AND size = $size
            """;
        cmd.Parameters.AddWithValue("$fid", fileId);
        cmd.Parameters.AddWithValue("$size", size);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null; // linha ausente OU size divergente ⇒ recálculo
        }

        var cachedMtime = DateTime.Parse(
            reader.GetString(1), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if ((cachedMtime - mtimeUtc.ToUniversalTime()).Duration() > MtimeTolerance)
        {
            return null; // mtime divergente além da tolerância conservadora ⇒ recálculo
        }

        return reader.IsDBNull(0) ? null : reader.GetString(0);
    }

    /// <summary>Insere ou substitui a linha da file_id (PK única — nunca duplica).</summary>
    public void Upsert(long fileId, long size, DateTime mtimeUtc, string? hashFull, string? hashPartial = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO cache_hashes
                (file_id, size, mtime_utc, hash_partial, hash_full, algorithm, hash_version, last_seen)
            VALUES ($fid, $size, $mtime, $hp, $hf, 'BLAKE3', 1, $seen)
            ON CONFLICT(file_id) DO UPDATE SET
                size = excluded.size,
                mtime_utc = excluded.mtime_utc,
                hash_partial = excluded.hash_partial,
                hash_full = excluded.hash_full,
                last_seen = excluded.last_seen
            """;
        cmd.Parameters.AddWithValue("$fid", fileId);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$mtime", ToIso(mtimeUtc));
        cmd.Parameters.AddWithValue("$hp", (object?)hashPartial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hf", (object?)hashFull ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seen", ToIso(DateTime.Now));
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
