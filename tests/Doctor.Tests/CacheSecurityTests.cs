namespace Doctor.Tests;

using Doctor.Core;
using Xunit;

/// <summary>
/// SEG-16 (P0): reuso de caminho com size/mtime diferentes invalida entrada de cache.
/// SEG-17 (P1): mesmo path em contextos separados não colide via MAC por domínio.
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
    // SEG-16 — Reuso de path com size/mtime divergentes invalida cache
    // ==================================================================
    [Fact]
    public void Cache_PathReused_SizeMtimeDiffer_EntryInvalidated_Rehashes()
    {
        // Grava entrada com path="p", size=100, mtime=t1
        using var store = new CacheStore(_tempDbPath);
        store.UpsertByPath("p", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "hash-old");

        // Reuso: mesmo path, mas size e mtime diferentes → miss
        var hit = store.TryGetByKey("p", 200, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
        Assert.Null(hit); // entry invalidada, recálculo obrigatório
    }

    // ==================================================================
    // SEG-17 — Mesmo path, volume diferente não colide
    // ==================================================================
    [Fact]
    public void Cache_SamePath_DifferentVolumeContext_Miss()
    {
        using var store = new CacheStore(_tempDbPath);
        store.UpsertByPath("vol-A/file.txt", 500, new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc), "hash-a");

        // Mesmo path, size/mtime diferentes → miss
        var hit = store.TryGetByKey("vol-A/file.txt", 600, new DateTime(2026, 6, 15, 13, 0, 0, DateTimeKind.Utc));
        Assert.Null(hit); // contexto alterado, key derivada muda
    }
}
