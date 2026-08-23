namespace Doctor.Core;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

/// <summary>
/// Exceção de integridade do cache (threat-model T-08, regra R8; card T19):
/// lançada quando o banco de cache apresenta violação de integridade — linha
/// cujo MAC não confere, schema adulterado, <c>schema_version</c>
/// incompatível ou estrutura SQLite corrompida. Fail-closed: a corrupção é
/// DISTINGUÍDA de ausência (miss) e nunca vira recálculo silencioso nem
/// devolve hash envenenado ao pipeline.
/// </summary>
public sealed class CacheCorruptedException : InvalidOperationException
{
    public CacheCorruptedException(string message)
        : base($"cache corrompido (fail-closed): {message}")
        => Reason = message;

    public CacheCorruptedException(string message, Exception innerException)
        : base($"cache corrompido (fail-closed): {message}", innerException)
        => Reason = message;

    /// <summary>Motivo técnico da rejeição (auditoria; sem dados de conteúdo).</summary>
    public string Reason { get; }
}

/// <summary>
/// Cache incremental SQLite (SPEC §12; ADR-0006; cards T11 t_c7004baf + T19
/// t_43803664 — identidade e fail-closed, consolidando S11-3/S11-4).
///
/// Schema v2 (T19), chave = BLAKE3(path,size,mtime) — NUNCA caminho absoluto:
///   cache_hashes(key_hex TEXT PK, size INTEGER, mtime_ticks INTEGER,
///                hash_partial TEXT, hash_full TEXT, algorithm TEXT NOT NULL,
///                hash_version INTEGER NOT NULL, entry_mac TEXT NOT NULL,
///                last_seen TEXT)
///   cache_meta(key TEXT PK, value TEXT NOT NULL)  -- schema_version etc.
///
/// Identidade (T-08/R8): a chave lógica é hash BLAKE3 da tupla canônica
/// (caminho normalizado, size, mtime ticks) com prefixo de domínio
/// "ccd-cache-key-v1" (receita pinada pelo teste P1). O caminho nunca é
/// persistido — renome/move mudam a chave ⇒ recálculo conservador; mesmo
/// estado ⇒ mesma chave (determinismo §3). Integridade por linha: entry_mac
/// = BLAKE3("ccd-cache-mac-v1" || key || algorithm || hash_version ||
/// hash_full/parcial) gravado no Upsert e REVALIDADO em toda leitura — linha
/// adulterada no disco ⇒ <see cref="CacheCorruptedException"/> (fail-closed;
/// veneno nunca é servido, corrupção ≠ miss).
///
/// Abertura fail-closed: header SQLite inválido, schema ausente/adulterado ou
/// schema_version incompatível ⇒ exceção na abertura. O banco do usuário
/// JAMAIS é recriado/apagado silenciosamente.
///
/// Microsoft.Data.Sqlite PINADO (8.0.8) via Directory.Packages.props (ADR-0001/0006,
/// risco R9). WAL; Pooling=false ⇒ conexão única real do processo (acesso exclusivo,
/// sem servidor, zero rede — §26). Banco padrão em %LOCALAPPDATA%/ConflictDoctor/cache.db;
/// caminho INJETÁVEL pelo construtor para testes (tmp).
///
/// Segurança (threat model T-08): TryGetByKey só devolve hash com size E mtime
/// batendo — mtime com tolerância CONSERVADORA de 2 s: NA DÚVIDA, RECALCULA.
/// Divergência de qualquer coluna-chave ⇒ miss ⇒ recálculo obrigatório; cache
/// venenoso nunca mascara o disco. Presença/ausência de hit jamais muda o
/// relatório emitido (determinismo §3).
/// </summary>
public sealed class CacheStore : IDisposable
{
    private const string DefaultRelativePath = "ConflictDoctor/cache.db";

    /// <summary>Tolerância conservadora de mtime (rounding FAT/NTFS): na dúvida, recalcula.</summary>
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Versão do schema v2 gerenciada por este código (cache_meta.schema_version).</summary>
    internal const int SchemaVersion = 2;

    /// <summary>Prefixo de domínio da receita v1 da chave — pinado pelo teste P1.</summary>
    internal const string KeyDomainTag = "ccd-cache-key-v1";

    /// <summary>Prefixo de domínio do MAC por linha (integridade T-08).</summary>
    private const string MacDomainTag = "ccd-cache-mac-v1";

    /// <summary>Algoritmo/hash version fixados pela receita v1 (ADR-0005/0006).</summary>
    private const string AlgorithmV1 = "BLAKE3";
    private const int HashVersionV1 = 1;

    private readonly SqliteConnection _connection;

    /// <summary>
    /// Abre o cache no caminho dado (injetável para testes). null usa o padrão do produto:
    /// %LOCALAPPDATA%/ConflictDoctor/cache.db (Unix: $XDG_DATA_HOME ou ~/.local/share).
    ///
    /// Fail-closed (T19): banco existente com header ilegível, estrutura corrompida
    /// ou schema v1 legado/incompatível lança <see cref="CacheCorruptedException"/>
    /// (v1 legado não tem entry_mac nem cache_meta — não é verificável ⇒ não é aberto).
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
            // Header/páginas corrompidos estouram aqui na primeira leitura:
            // fail-closed nomeado, sem reciclar o banco do usuário.
            _connection.Dispose();
            throw new CacheCorruptedException($"estrutura SQLite inválida ({ex.SqliteErrorCode}: {ex.Message})", ex);
        }
    }

    /// <summary>
    /// Garante o schema v2 em banco NOVO ou valida integridade estrutural de banco
    /// EXISTENTE (card T19): colunas esperadas presentes, cache_meta.schema_version
    /// compatível e verificação estrutural limpa. Qualquer desvio ⇒
    /// <see cref="CacheCorruptedException"/> — o banco nunca é recriado por cima.
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
                    "banco sem cache_hashes mas com outras tabelas — schema desconhecido");
            }

            CreateSchema();
            return;
        }

        ValidateExistingSchema();
    }

    /// <summary>Cria schema v2 completo (tabela + meta) num banco novo.</summary>
    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS cache_hashes (
                key_hex      TEXT NOT NULL PRIMARY KEY, -- BLAKE3 hex de (path,size,mtime) — nunca o caminho
                size         INTEGER NOT NULL,          -- bytes; divergência invalida
                mtime_ticks  INTEGER NOT NULL,          -- DateTime.Ticks UTC; tolerância 2 s na leitura
                hash_partial TEXT,                      -- BLAKE3 janelas v1 (ADR-0005 §3)
                hash_full    TEXT,                      -- só completo genuíno (≤128 KiB ou L3)
                algorithm    TEXT NOT NULL,             -- 'BLAKE3' fixo na receita v1
                hash_version INTEGER NOT NULL,          -- 1
                entry_mac    TEXT NOT NULL,             -- BLAKE3 das colunas protegidas (integridade T-08)
                last_seen    TEXT NOT NULL              -- ISO-8601 UTC da última gravação (poda)
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
    /// Validação estrutural de banco existente (fail-closed): colunas completas,
    /// schema_version presente e compatível, verificação estrutural limpa.
    /// </summary>
    private void ValidateExistingSchema()
    {
        // 1. Colunas esperadas — schema adulterado (coluna de integridade
        //    removida, tipos trocados) não é aberto.
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
                $"colunas ausentes/adulteradas em cache_hashes: {string.Join(", ", missing)}");
        }

        // 2. cache_meta presente e schema_version compatível.
        var metaExists = (long)(QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cache_meta'") ?? 0L) == 1L;
        if (!metaExists)
        {
            throw new CacheCorruptedException("cache_meta ausente — schema incompatível");
        }

        var storedVersion = QueryScalar(
            "SELECT value FROM cache_meta WHERE key = 'schema_version'") as string;
        if (storedVersion is null
            || !int.TryParse(storedVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            || version != SchemaVersion)
        {
            throw new CacheCorruptedException(
                $"schema_version '{storedVersion ?? "(ausente)"}' incompatível com {SchemaVersion}");
        }

        // 3. Verificação estrutural profunda: páginas corrompidas (bit flip em
        //    disco) são detectadas AQUI, na abertura — nunca depois, "porque
        //    parece funcionar".
        var quick = QueryScalar("PRAGMA quick_check(1)") as string;
        if (!string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new CacheCorruptedException($"verificação estrutural SQLite: {quick ?? "(sem resposta)"}");
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
    /// Chave lógica v1 (card T19; pinada pelo teste P1): BLAKE3 do fluxo canônico
    /// <c>"ccd-cache-key-v1" || UTF-8(caminho) || size int64 BE || mtime.Ticks int64 BE</c>,
    /// em hex minúscula de 64 caracteres. O caminho participa SÓ da derivação —
    /// nunca é persistido (prova P3). Mesmo estado ⇒ mesma chave (§3).
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
    /// MAC por linha (integridade T-08/R8): BLAKE3 de
    /// <c>"ccd-cache-mac-v1" || key_hex || algorithm || hash_version ||
    /// size || mtime_ticks || hash_full || hash_partial</c> com separadores '\n'.
    /// Gravado no Upsert e revalidado em TODA leitura sobre os valores LIDOS do
    /// disco: qualquer coluna de decisão adulterada fora da API (hash, size,
    /// mtime, algorithm, hash_version) quebra o MAC ⇒
    /// <see cref="CacheCorruptedException"/> — o veneno nunca é servido.
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
    /// Grava (ou substitui) a entrada derivada de (path,size,mtime). O caminho
    /// NÃO é persistido — só a chave BLAKE3 (prova P3). O MAC cobre todas as
    /// colunas de decisão: qualquer edição fora da API invalida a linha.
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
    /// hash em cache somente com correspondência EXATA da chave derivada de
    /// (path,size,mtime ≤ 2 s). Integridade primeiro (T-08): linha presente com
    /// entry_mac divergente ⇒ <see cref="CacheCorruptedException"/> (fail-closed;
    /// corrupção é DISTINTA de miss). Linha ausente ou metadado divergente ⇒
    /// null ⇒ recálculo. Nunca promove parcial a completo.
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
                return null; // ausente ⇒ miss ⇒ recálculo (comportamento calmo)
            }

            storedSize = reader.GetInt64(0);
            storedMtime = reader.GetInt64(1);
            storedHashPartial = reader.IsDBNull(2) ? null : reader.GetString(2);
            storedHashFull = reader.IsDBNull(3) ? null : reader.GetString(3);
            storedAlgorithm = reader.GetString(4);
            storedHashVersion = reader.GetInt64(5) switch { _ => checked((int)reader.GetInt64(5)) };
            storedMac = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        // Integridade por linha ANTES de qualquer uso do valor (fail-closed).
        // Guardas de receita (R8): version 1 e algoritmo BLAKE3 — downgrade
        // forçado em column-level é rejeitado pelo MAC, mas o guard de
        // receita garante que nenhuma linha fora da receita v1 é aceita.
        if (storedAlgorithm != AlgorithmV1 || storedHashVersion != HashVersionV1)
        {
            throw new CacheCorruptedException(
                $"receita v1 inválida na linha {keyHex[..12]}… (algorithm={storedAlgorithm}, hash_version={storedHashVersion} — esperado {AlgorithmV1}/{HashVersionV1})");
        }

        var expectedMac = ComputeEntryMac(keyHex, storedAlgorithm, storedHashVersion, storedSize, storedMtime, storedHashFull, storedHashPartial);
        if (storedMac is null || !FixedTimeEquals(storedMac, expectedMac))
        {
            throw new CacheCorruptedException($"entry_mac divergente na linha {keyHex[..12]}… — conteúdo adulterado");
        }

        // Metadados divergentes ⇒ miss ⇒ recálculo (nunca exceção).
        if (storedSize != size
            || (TimeSpan.FromTicks(storedMtime - mtimeUtc.ToUniversalTime().Ticks)).Duration() > MtimeTolerance)
        {
            return null;
        }

        return storedHashFull;
    }

    /// <summary>Comparação byte a byte sem curto-circuito (higiene, não segredo).</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) { return false; }
        var diff = 0;
        for (var i = 0; i < a.Length; i++) { diff |= a[i] ^ b[i]; }
        return diff == 0;
    }

    /// <summary>Ponto de escape p/ testes: SQL direto (fixtures simulam adulteração externa).</summary>
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
