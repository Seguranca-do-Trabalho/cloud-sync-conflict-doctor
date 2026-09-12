namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T11 (t_c7004baf) — CacheStore base behavior, migrated to
/// T19 (t_43803664) v2 identity: logical key is BLAKE3(path,size,mtime) —
/// legacy file_id no longer participates in identity (threat-model T-08, R8).
///
/// Three tests with temporary database ON DISK (no in-memory):
///   1. Open creates schema v2 and TryGetByKey on empty cache returns null;
///   2. UpsertByPath inserts, TryGetByKey returns hash_full; second write
///      for SAME key replaces row (UPSERT — PK key_hex, never duplicates);
///   3. TryGetByKey invalidates when size OR mtime diverge — divergence ⇒ null ⇒ recompute.
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

        // Open created schema v2: table + meta with schema_version.
        using (var store2 = new CacheStore(_tempDbPath))
        {
            var count = (long)(store2.QueryScalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cache_hashes'") ?? 0L);
            Assert.Equal(1L, count);

            var version = (string)store2.QueryScalar("SELECT value FROM cache_meta WHERE key = 'schema_version'")!;
            Assert.Equal("2", version);
        }

        Assert.Null(new CacheStore(_tempDbPath).TryGetByKey("/nothing/here.bin", size: 100, mtimeUtc: DateTime.UtcNow));
    }

    [Fact]
    public void UpsertByPath_InsertsThenReplaces_TryGetByKeyReturnsCurrentHashFull()
    {
        using var store = new CacheStore(_tempDbPath);
        var mtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        store.UpsertByPath("/docs/contract.docx", size: 500, mtimeUtc: mtime, hashFull: "aa11");

        Assert.Equal("aa11", store.TryGetByKey("/docs/contract.docx", size: 500, mtimeUtc: mtime));

        // Second write for SAME tuple (path,size,mtime): UPSERT replaces —
        // PK is BLAKE3 key, never duplicates row.
        store.UpsertByPath("/docs/contract.docx", size: 500, mtimeUtc: mtime, hashFull: "bb22");

        Assert.Equal("bb22", store.TryGetByKey("/docs/contract.docx", size: 500, mtimeUtc: mtime));

        var rows = (long)store.QueryScalar("SELECT COUNT(*) FROM cache_hashes")!;
        Assert.Equal(1L, rows);
    }

    [Fact]
    public void TryGetByKey_Invalidates_WhenSizeOrMtimeDiverge()
    {
        using var store = new CacheStore(_tempDbPath);
        var cachedMtime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        store.UpsertByPath("/docs/spreadsheet.xlsx", size: 2048, mtimeUtc: cachedMtime, hashFull: "cc33");

        // Exact same key: hit.
        Assert.NotNull(store.TryGetByKey("/docs/spreadsheet.xlsx", size: 2048, mtimeUtc: cachedMtime));

        // Divergent size ⇒ divergent derived key ⇒ miss ⇒ recalculation.
        Assert.Null(store.TryGetByKey("/docs/spreadsheet.xlsx", size: 4096, mtimeUtc: cachedMtime));

        // Divergent mtime (> 2s tolerance, when in doubt recalculate) ⇒ invalidate.
        Assert.Null(store.TryGetByKey("/docs/spreadsheet.xlsx", size: 2048, mtimeUtc: cachedMtime.AddSeconds(30)));

        // Divergent path with same size/mtime ⇒ another key ⇒ miss (identity includes path).
        Assert.Null(store.TryGetByKey("/docs/other-name.xlsx", size: 2048, mtimeUtc: cachedMtime));
    }
}
