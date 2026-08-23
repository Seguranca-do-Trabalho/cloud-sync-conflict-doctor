namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T11 (t_c7004baf) — CacheStore SQLite (escopo reduzido do orquestrador).
///
/// Três testes com banco temporário EM DISCO (sem in-memory):
///   1. EnsureTable cria o schema cache_hashes e TryGet em tabela vazia retorna null;
///   2. Upsert insere, TryGet devolve hash_full; segundo Upsert para a MESMA file_id
///      substitui a linha (UPSERT, sem duplicar) e TryGet reflete o novo valor;
///   3. TryGet invalida quando size OU mtime divergem — divergência ⇒ null ⇒ recálculo.
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
    public void EnsureTable_CreatesSchema_And_TryGetOnEmptyCache_ReturnsNull()
    {
        using var store = new CacheStore(_tempDbPath);

        Assert.True(store.EnsureTable());

        // Tabela existe: consulta direta prova a criação do schema v1 do orquestrador.
        using (var store2 = new CacheStore(_tempDbPath))
        {
            var count = (long)(store2.QueryScalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cache_hashes'") ?? 0L);
            Assert.Equal(1L, count);
        }

        Assert.Null(new CacheStore(_tempDbPath).TryGet(fileId: 42, size: 100, mtimeUtc: DateTime.UtcNow));
    }

    [Fact]
    public void Upsert_InsertsThenReplaces_TryGetReturnsCurrentHashFull()
    {
        using var store = new CacheStore(_tempDbPath);
        var mtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        store.Upsert(fileId: 7, size: 500, mtimeUtc: mtime, hashFull: "aa11");

        Assert.Equal("aa11", store.TryGet(fileId: 7, size: 500, mtimeUtc: mtime));

        // Segunda gravação para a MESMA file_id: UPSERT substitui — PK file_id, nunca duplica.
        store.Upsert(fileId: 7, size: 500, mtimeUtc: mtime, hashFull: "bb22");

        Assert.Equal("bb22", store.TryGet(fileId: 7, size: 500, mtimeUtc: mtime));

        var rows = (long)store.QueryScalar("SELECT COUNT(*) FROM cache_hashes")!;
        Assert.Equal(1L, rows);
    }

    [Fact]
    public void TryGet_Invalidates_WhenSizeOrMtimeDiverge()
    {
        using var store = new CacheStore(_tempDbPath);
        var cachedMtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        store.Upsert(fileId: 9, size: 2048, mtimeUtc: cachedMtime, hashFull: "cc33");

        // Mesma chave exata: hit.
        Assert.NotNull(store.TryGet(fileId: 9, size: 2048, mtimeUtc: cachedMtime));

        // Size divergente ⇒ invalida.
        Assert.Null(store.TryGet(fileId: 9, size: 4096, mtimeUtc: cachedMtime));

        // Mtime divergente (> tolerância de 2 s, na dúvida recalcula) ⇒ invalida.
        Assert.Null(store.TryGet(fileId: 9, size: 2048, mtimeUtc: cachedMtime.AddSeconds(30)));
    }
}
