namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T11 (t_c7004baf) — comportamento base do CacheStore, migrado para a
/// identidade v2 do T19 (t_43803664): a chave lógica é BLAKE3(path,size,mtime) —
/// o file_id legado não participa mais da identidade (threat-model T-08, R8).
///
/// Três testes com banco temporário EM DISCO (sem in-memory):
///   1. Abertura cria schema v2 e TryGetByKey em cache vazio retorna null;
///   2. UpsertByPath insere, TryGetByKey devolve hash_full; segunda gravação
///      para a MESMA chave substitui a linha (UPSERT — PK key_hex, nunca duplica);
///   3. TryGetByKey invalida quando size OU mtime divergem — divergência ⇒ null ⇒ recálculo.
/// </summary>
public class CacheStoreTests : IDisposable
{
    private readonly string _tempDbPath;

    public CacheStoreTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"cache-t11-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try { File.Delete(_tempDbPath + suffix); } catch (IOException) { }
        }
    }

    [Fact]
    public void Open_CreatesSchemaV2_And_TryGetByKeyOnEmptyCache_ReturnsNull()
    {
        using var store = new CacheStore(_tempDbPath);

        // Abertura criou schema v2: tabela + meta com schema_version.
        using (var store2 = new CacheStore(_tempDbPath))
        {
            var count = (long)(store2.QueryScalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cache_hashes'") ?? 0L);
            Assert.Equal(1L, count);

            var version = (string)store2.QueryScalar("SELECT value FROM cache_meta WHERE key = 'schema_version'")!;
            Assert.Equal("2", version);
        }

        Assert.Null(new CacheStore(_tempDbPath).TryGetByKey("/nada/aqui.bin", size: 100, mtimeUtc: DateTime.UtcNow));
    }

    [Fact]
    public void UpsertByPath_InsertsThenReplaces_TryGetByKeyReturnsCurrentHashFull()
    {
        using var store = new CacheStore(_tempDbPath);
        var mtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        store.UpsertByPath("/docos/contrato.docx", size: 500, mtimeUtc: mtime, hashFull: "aa11");

        Assert.Equal("aa11", store.TryGetByKey("/docos/contrato.docx", size: 500, mtimeUtc: mtime));

        // Segunda gravação para a MESMA tupla (path,size,mtime): UPSERT substitui —
        // PK é a chave BLAKE3, nunca duplica linha.
        store.UpsertByPath("/docos/contrato.docx", size: 500, mtimeUtc: mtime, hashFull: "bb22");

        Assert.Equal("bb22", store.TryGetByKey("/docos/contrato.docx", size: 500, mtimeUtc: mtime));

        var rows = (long)store.QueryScalar("SELECT COUNT(*) FROM cache_hashes")!;
        Assert.Equal(1L, rows);
    }

    [Fact]
    public void TryGetByKey_Invalidates_WhenSizeOrMtimeDiverge()
    {
        using var store = new CacheStore(_tempDbPath);
        var cachedMtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        store.UpsertByPath("/docos/planilha.xlsx", size: 2048, mtimeUtc: cachedMtime, hashFull: "cc33");

        // Mesma chave exata: hit.
        Assert.NotNull(store.TryGetByKey("/docos/planilha.xlsx", size: 2048, mtimeUtc: cachedMtime));

        // Size divergente ⇒ chave derivada divergente ⇒ miss ⇒ recálculo.
        Assert.Null(store.TryGetByKey("/docos/planilha.xlsx", size: 4096, mtimeUtc: cachedMtime));

        // Mtime divergente (> tolerância de 2 s, na dúvida recalcula) ⇒ invalida.
        Assert.Null(store.TryGetByKey("/docos/planilha.xlsx", size: 2048, mtimeUtc: cachedMtime.AddSeconds(30)));

        // Caminho divergente com mesmo size/mtime ⇒ outra chave ⇒ miss (identidade inclui o caminho).
        Assert.Null(store.TryGetByKey("/docos/outro-nome.xlsx", size: 2048, mtimeUtc: cachedMtime));
    }
}
