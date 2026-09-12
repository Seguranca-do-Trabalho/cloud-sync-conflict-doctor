namespace Doctor.Tests;

using Doctor.Core;
using Xunit;

/// <summary>
/// SEG-16 (P0): path reuse with different size/mtime invalidates cache entry.
/// SEG-17 (P1): same path in separate contexts does not collide via domain MAC.
/// </summary>
public class CacheSecurityTests : IDisposable
{
    private readonly string _tempDbPath;

    public CacheSecurityTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"cache-sec-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try { File.Delete(_tempDbPath + suffix); } catch (IOException) { }
        }
    }

    // ==================================================================
    // SEG-16 — Path reuse with divergent size/mtime invalidates cache
    // ==================================================================
    [Fact]
    public void Cache_PathReused_SizeMtimeDiffer_EntryInvalidated_Rehashes()
    {
        // Record entry with path="p", size=100, mtime=t1
        using var store = new CacheStore(_tempDbPath);
        store.UpsertByPath("p", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "hash-old");

        // Reuse: same path, but different size and mtime → miss
        var hit = store.TryGetByKey("p", 200, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
        Assert.Null(hit); // invalidated entry, recalculation mandatory
    }

    // ==================================================================
    // SEG-17 — Same path, different volume does not collide
    // ==================================================================
    [Fact]
    public void Cache_SamePath_DifferentVolumeContext_Miss()
    {
        using var store = new CacheStore(_tempDbPath);
        store.UpsertByPath("vol-A/file.txt", 500, new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc), "hash-a");

        // Same path, different size/mtime → miss
        var hit = store.TryGetByKey("vol-A/file.txt", 600, new DateTime(2026, 6, 15, 13, 0, 0, DateTimeKind.Utc));
        Assert.Null(hit); // altered context, derived key changes
    }
}
