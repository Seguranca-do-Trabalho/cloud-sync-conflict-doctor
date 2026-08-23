namespace Doctor.Tests;

using System.Buffers.Binary;
using System.Text;
using Doctor.Core;

/// <summary>
/// T19 (t_43803664) — Identidade do cache e fail-closed (consolida S11-3 TOCTOU +
/// S11-4 cache poisoning; threat-model T-08, regra R8; SPEC §12; ADR-0006).
///
/// Família CACHE-POISON (6): a chave lógica é hash BLAKE3 de (path,size,mtime) —
/// NUNCA o caminho absoluto persistido — e linha adulterada no banco jamais devolve
/// hash (fail-closed: exceção, nunca veneno nem miss silencioso).
/// Família CACHE-INTEGRITY (4): banco com header/colunas/schema_version/páginas
/// violados falha NA ABERTURA em vez de reciclar silenciosamente.
/// Família OP-ID (3): operation_id = função determinística do CONTEÚDO do ScanResult.
/// </summary>
public class CachePoisoningTests : IDisposable
{
    private readonly string _tempDbPath;

    public CachePoisoningTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"cache-t19-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try { File.Delete(_tempDbPath + suffix); } catch (IOException) { }
        }
    }

    // ==================================================================
    // CACHE-POISON 1 — A chave deriva de BLAKE3(path,size,mtime), não do
    // caminho puro. Vetor independente: o teste reconstrói os bytes canônicos
    // da receita v1 e compara com o hash do produto (não reimplementa nada).
    // ==================================================================
    [Fact]
    public void P1_ComputeKey_IsBlake3OfPathSizeMtime_NotTheRawPath()
    {
        const string path = "/opt/ccd-t19/contrato_v1.docx";
        var mtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        var key = CacheStore.ComputeKey(path, size: 500, mtimeUtc: mtime);

        var expected = Convert.ToHexString(
            Blake3.Hasher.Hash(CanonicalKeyBytes(path, 500, mtime)).AsSpan())
            .ToLowerInvariant();

        Assert.Equal(64, key.Length);                    // BLAKE3 32 B ⇒ 64 hex
        Assert.Equal(expected, key);                     // receita v1 exata
    }

    /// <summary>Receita v1 da chave (pinada por P1): ASCII "ccd-cache-key-v1" +
    /// UTF-8 do caminho normalizado + size int64 big-endian + ticks UTC int64
    /// big-endian. Domínio separado impede colisão com outros usos de BLAKE3.</summary>
    private static byte[] CanonicalKeyBytes(string normalizedPath, long size, DateTime mtime)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("ccd-cache-key-v1"));
        ms.Write(Encoding.UTF8.GetBytes(normalizedPath));
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, size);
        ms.Write(buf);
        BinaryPrimitives.WriteInt64BigEndian(buf, mtime.Ticks);
        ms.Write(buf);
        return ms.ToArray();
    }

    // ==================================================================
    // CACHE-POISON 2 — Caminhos distintos ⇒ chaves distintas; mesmo estado
    // ⇒ MESMA chave (determinismo §3); a chave nunca contém trecho do caminho.
    // ==================================================================
    [Fact]
    public void P2_ComputeKey_DistinguishesPaths_IsDeterministic_NeverContainsPath()
    {
        const long size = 4096;
        var mtime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var k1 = CacheStore.ComputeKey("/mnt/a/relatorio.xlsx", size, mtime);
        var k2 = CacheStore.ComputeKey("/mnt/b/relatorio.xlsx", size, mtime);
        var k1again = CacheStore.ComputeKey("/mnt/a/relatorio.xlsx", size, mtime);

        Assert.NotEqual(k1, k2);                 // caminho participa da identidade
        Assert.Equal(k1, k1again);               // mesmo estado ⇒ mesma chave
        Assert.DoesNotContain("relatorio", k1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/mnt", k1, StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================
    // CACHE-POISON 3 — O caminho absoluto NUNCA é persistido: grava pela API
    // e varre os bytes crus do banco (db + wal) procurando o caminho em UTF-8
    // e UTF-16LE. Zero ocorrências — E a entrada continua recuperável.
    // ==================================================================
    [Fact]
    public void P3_UpsertByPath_AbsolutePathNeverPersistedInDatabaseBytes()
    {
        const string secretPath = "/home/user/doctos/financeiro/pessoal/salario-agosto.xlsx";
        var mtime = new DateTime(2026, 8, 23, 9, 30, 0, DateTimeKind.Utc);

        using (var store = new CacheStore(_tempDbPath))
        {
            store.UpsertByPath(secretPath, size: 12345, mtimeUtc: mtime,
                hashFull: "abcdef0123456789");
        }

        var raw = ReadDatabaseBytes();
        Assert.True(raw.Length > 0, "banco deveria existir após Upsert");

        Assert.False(ContainsSequence(raw, Encoding.UTF8.GetBytes(secretPath)),
            "caminho absoluto vazou em UTF-8 nos bytes do banco");
        Assert.False(ContainsSequence(raw, Encoding.Unicode.GetBytes(secretPath)),
            "caminho absoluto vazou em UTF-16LE nos bytes do banco");

        // Mesmo assim, a identidade deriva determinísticamente:
        using var store2 = new CacheStore(_tempDbPath);
        Assert.Equal("abcdef0123456789",
            store2.TryGetByKey(secretPath, size: 12345, mtimeUtc: mtime));
    }

    // ==================================================================
    // CACHE-POISON 4/5/6 — Linha envenenada FORA da API (simula usuário/
    // malware no disco, superfície S2): hash_full trocado, size falsificado,
    // algorithm rebaixado a MD5 ⇒ TryGetByKey LANÇA CacheCorruptedException.
    // O veneno nunca é servido; corrupção se distingue de ausência.
    // ==================================================================
    [Fact]
    public void P4_PoisonedHashFull_TryGetFailsClosed()
    {
        const string path = "/x/a.bin";
        var mtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        using (var store = new CacheStore(_tempDbPath))
        {
            store.UpsertByPath(path, size: 1000, mtimeUtc: mtime, hashFull: "cafe01");
        }

        TamperRow(path, 1000, mtime, "hash_full", "deadbeef");

        using var victim = new CacheStore(_tempDbPath);
        var ex = Assert.Throws<CacheCorruptedException>(() =>
            victim.TryGetByKey(path, size: 1000, mtimeUtc: mtime));
        Assert.Contains("entry_mac", ex.Message);
    }

    [Fact]
    public void P5_PoisonedSizeColumn_TryGetFailsClosed_FalsifiedKeyYieldsMiss()
    {
        const string path = "/y/b.bin";
        var mtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        using (var store = new CacheStore(_tempDbPath))
        {
            store.UpsertByPath(path, size: 2000, mtimeUtc: mtime, hashFull: "beef02");
        }

        TamperRow(path, 2000, mtime, "size", "999999");

        using var victim = new CacheStore(_tempDbPath);

        // Consulta legítima (size original): linha existe mas MAC não confere ⇒ falha fechada.
        var ex = Assert.Throws<CacheCorruptedException>(() =>
            victim.TryGetByKey(path, size: 2000, mtimeUtc: mtime));
        Assert.Contains("entry_mac", ex.Message);

        // Consulta com o size FALSIFICADO: chave derivada difere ⇒ linha não é
        // encontrada ⇒ null (miss ⇒ recálculo). O veneno não é servível nem assim.
        Assert.Null(victim.TryGetByKey(path, size: 999999, mtimeUtc: mtime));
    }

    [Fact]
    public void P6_PoisonedAlgorithmColumn_TryGetFailsClosed_NoDowngrade()
    {
        const string path = "/z/c.bin";
        var mtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        using (var store = new CacheStore(_tempDbPath))
        {
            store.UpsertByPath(path, size: 3000, mtimeUtc: mtime, hashFull: "feed03");
        }

        TamperRow(path, 3000, mtime, "algorithm", "MD5"); // tentativa de downgrade

        using var victim = new CacheStore(_tempDbPath);
        var ex = Assert.Throws<CacheCorruptedException>(() =>
            victim.TryGetByKey(path, size: 3000, mtimeUtc: mtime));
        Assert.Contains("algorithm", ex.Message);
    }

    /// <summary>
    /// Corrupção fora da API: UPDATE SQL direto na linha cuja chave deriva de
    /// (path,size,mtime) — simula adulteração do arquivo SQLite no disco.
    /// </summary>
    private void TamperRow(string path, long size, DateTime mtime, string column, string value)
    {
        using var helper = new CacheStore(_tempDbPath);
        using var cmd = helper.CreateCommandForTests();
        cmd.CommandText = $"UPDATE cache_hashes SET {column} = $v WHERE key_hex = $k";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$k", CacheStore.ComputeKey(path, size, mtime));
        cmd.ExecuteNonQuery();
    }

    // ==================================================================
    // INTEGRITY 1 — Header SQLite apagado ⇒ abertura falha fechada
    // (nunca reciclar/recriar um banco que pode conter dados válidos).
    // ==================================================================
    [Fact]
    public void I1_HeaderCorrupted_OpenFailsClosed()
    {
        using (var store = new CacheStore(_tempDbPath))
        {
            store.UpsertByPath("/i/a.bin", size: 10,
                mtimeUtc: DateTime.UtcNow, hashFull: "aa");
        }

        var rawBytes = File.ReadAllBytes(_tempDbPath);
        Array.Clear(rawBytes, 0, 16); // "SQLite format 3\0"
        File.WriteAllBytes(_tempDbPath, rawBytes);

        var ex = Assert.ThrowsAny<Exception>(() => _ = new CacheStore(_tempDbPath));
        Assert.True(ex is CacheCorruptedException or Microsoft.Data.Sqlite.SqliteException,
            $"esperado fail-closed na abertura; veio {ex.GetType().Name}: {ex.Message}");
    }

    // ==================================================================
    // INTEGRITY 2 — Schema adulterado (coluna entry_mac removida) ⇒
    // abertura falha fechada: sem coluna de integridade não há cache confiável.
    // ==================================================================
    [Fact]
    public void I2_DroppedIntegrityColumn_OpenFailsClosed()
    {
        using (var store = new CacheStore(_tempDbPath))
        {
            store.UpsertByPath("/i/b.bin", size: 20,
                mtimeUtc: DateTime.UtcNow, hashFull: "bb");
        }

        using (var store = new CacheStore(_tempDbPath))
        {
            using var cmd = store.CreateCommandForTests();
            cmd.CommandText = """
                ALTER TABLE cache_hashes RENAME TO cache_hashes_tampered;
                CREATE TABLE cache_hashes AS SELECT key_hex, size FROM cache_hashes_tampered;
                DROP TABLE cache_hashes_tampered;
                """;
            cmd.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Exception>(() => _ = new CacheStore(_tempDbPath));
    }

    // ==================================================================
    // INTEGRITY 3 — schema_version incompatível ⇒ fail-closed nomeado
    // (bancos de outra versão de schema não são abertos nem migrados às cegas).
    // ==================================================================
    [Fact]
    public void I3_IncompatibleSchemaVersion_OpenFailsClosed_NamedError()
    {
        using (var store = new CacheStore(_tempDbPath))
        {
            store.UpsertByPath("/i/c.bin", size: 30,
                mtimeUtc: DateTime.UtcNow, hashFull: "cc");
        }

        using (var store = new CacheStore(_tempDbPath))
        {
            using var cmd = store.CreateCommandForTests();
            cmd.CommandText =
                "UPDATE cache_meta SET value = '999' WHERE key = 'schema_version'";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<CacheCorruptedException>(() => _ = new CacheStore(_tempDbPath));
        Assert.Contains("schema_version", ex.Message);
    }

    // ==================================================================
    // INTEGRITY 4 — Corrupção profunda de página (último terço do arquivo
    // inteiro invertido) ⇒ abertura falha fechada via verificação de
    // integridade estrutural. Nunca abre "porque ainda parece funcionar".
    // ==================================================================
    [Fact]
    public void I4_PageLevelCorruption_OpenFailsClosed_StructuralCheck()
    {
        for (var i = 0; i < 50; i++)
        {
            using (var store = new CacheStore(_tempDbPath))
            {
                store.UpsertByPath($"/bulk/f{i}.bin", size: 100 + i,
                    mtimeUtc: new DateTime(2026, 8, 1, 0, i % 60, 0, DateTimeKind.Utc),
                    hashFull: $"hash{i:D4}");
            }
        }

        var rawBytes = File.ReadAllBytes(_tempDbPath);
        for (var i = rawBytes.Length * 2 / 3; i < rawBytes.Length; i++)
        {
            rawBytes[i] ^= 0xFF;
        }
        File.WriteAllBytes(_tempDbPath, rawBytes);

        var ex = Assert.ThrowsAny<Exception>(() => _ = new CacheStore(_tempDbPath));
        Assert.True(ex is CacheCorruptedException or Microsoft.Data.Sqlite.SqliteException,
            $"esperado fail-closed na abertura; veio {ex.GetType().Name}: {ex.Message}");
    }

    // ==================================================================
    // OPERATION_ID — fingerprint determinístico do CONTEÚDO do ScanResult
    // (card T19: "operation_id = hash do conteudo do ScanResult").
    // ==================================================================

    /// <summary>O1 — mesmo conteúdo ⇒ mesmo id, qualquer ordem física das
    /// listas (ordem canônica aplicada antes do hash — §3).</summary>
    [Fact]
    public void O1_OperationId_IsContentFingerprint_IgnoringPhysicalOrder()
    {
        var id1 = ScanFingerprint.OperationId(SampleScanResult(shuffleMembers: false));
        var id2 = ScanFingerprint.OperationId(SampleScanResult(shuffleMembers: true));

        Assert.False(string.IsNullOrWhiteSpace(id1));
        Assert.Equal(id1, id2);                  // ordem física não decide
        Assert.Matches("^[0-9a-f]{16}$", id1);   // 64 bits truncados, hex minúscula
    }

    /// <summary>O2 — mudou conteúdo ⇒ mudou id (o id é FUNÇÃO do conteúdo).</summary>
    [Fact]
    public void O2_OperationId_ChangesWhenScanResultContentChanges()
    {
        var baseId = ScanFingerprint.OperationId(SampleScanResult(shuffleMembers: false));

        var sample = SampleScanResult(shuffleMembers: false);
        var conflicted = sample.RealConflicts[0] with
        {
            Files = sample.RealConflicts[0].Files
                .Append(new ConflictMember("/a/outro.docx", "hash-zz"))
                .ToList(),
        };
        var modified = sample with { RealConflicts = new[] { conflicted } };

        Assert.NotEqual(baseId, ScanFingerprint.OperationId(modified));
    }

    /// <summary>O3 — hash divergente ⇒ id divergente (duas classificações
    /// diferentes nunca compartilham operation_id).</summary>
    [Fact]
    public void O3_OperationId_DifferentHashes_YieldDifferentIds()
    {
        var sample = SampleScanResult(shuffleMembers: false);
        var other = sample with
        {
            IdenticalDuplicates = new[]
            {
                sample.IdenticalDuplicates[0] with { Hash = "hash-outro" },
            },
        };

        Assert.NotEqual(
            ScanFingerprint.OperationId(sample),
            ScanFingerprint.OperationId(other));
    }

    /// <summary>ScanResult mínimo: 1 grupo, 1 duplicata idêntica (2 membros),
    /// 1 conflito real (2 membros).</summary>
    private static ScanResult SampleScanResult(bool shuffleMembers)
    {
        FileEntry Entry(string p, long s, string fid) => new()
        {
            Path = p,
            Size = s,
            MtimeUtc = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
            Attributes = FileAttributes.Archive,
            VolumeId = "vol-1",
            FileId = fid,
        };

        var eA = Entry("/a/relatorio.docx", 1024, "fid-a");
        var eB = Entry("/b/relatorio.docx", 1024, "fid-b");
        var eC = Entry("/c/contrato.docx", 2048, "fid-c");

        var dupFiles = shuffleMembers ? new[] { eB, eA } : new[] { eA, eB };
        var conflictMembers = (shuffleMembers ? new[] { eC, eA } : new[] { eA, eC })
            .Select(e => new ConflictMember(e.Path, $"h-{e.FileId}"))
            .ToList();

        return new ScanResult(
            Groups: new[] { new ConflictGroup("relatorio.docx", 1024, dupFiles) },
            IdenticalDuplicates: new[] { new IdenticalDuplicate("hash-ab", 1024, dupFiles) },
            RealConflicts: new[]
            {
                new RealConflict("contrato.docx", 2048, conflictMembers),
            });
    }

    // ---------------- helpers ----------------

    /// <summary>Lê db + wal (o que existir) após fechar a conexão.</summary>
    private byte[] ReadDatabaseBytes()
    {
        var bytes = new List<byte>();
        foreach (var suffix in new[] { string.Empty, "-wal" })
        {
            var f = _tempDbPath + suffix;
            if (File.Exists(f))
            {
                bytes.AddRange(File.ReadAllBytes(f));
            }
        }
        return bytes.ToArray();
    }

    /// <summary>Busca de sequência de bytes (subsequência contígua).</summary>
    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) { return true; }
        }
        return false;
    }
}
