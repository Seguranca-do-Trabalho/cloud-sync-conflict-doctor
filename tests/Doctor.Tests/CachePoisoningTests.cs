namespace Doctor.Tests;

using System.Buffers.Binary;
using System.Text;
using Doctor.Core;

/// <summary>
/// T19 (t_43803664) — Cache identity and fail-closed (consolidates S11-3 TOCTOU +
/// S11-4 cache poisoning; threat-model T-08, rule R8; SPEC §12; ADR-0006).
///
/// CACHE-POISON family (6): logical key is BLAKE3 hash of (path,size,mtime) —
/// NEVER the persisted absolute path — and tampered row in database never returns
/// hash (fail-closed: exception, never poison or silent miss).
/// CACHE-INTEGRITY family (4): DB with violated header/columns/schema_version/pages
/// fails ON OPEN instead of silently recycling.
/// OP-ID family (3): operation_id = deterministic function of ScanResult CONTENT.
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
    // CACHE-POISON 1 — Key derives from BLAKE3(path,size,mtime), not raw
    // path. Independent vector: test reconstructs canonical bytes
    // from recipe v1 and compares against product hash (no reimplementation).
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
        Assert.Equal(expected, key);                     // exact v1 recipe
    }

    /// <summary>Key v1 recipe (pinned by P1): ASCII "ccd-cache-key-v1" +
    /// UTF-8 of normalized path + size int64 big-endian + ticks UTC int64
    /// big-endian. Domain separation prevents collision with other BLAKE3 uses.</summary>
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
    // CACHE-POISON 2 — Distinct paths ⇒ distinct keys; same state
    // ⇒ SAME key (determinism §3); key never contains path snippet.
    // ==================================================================
    [Fact]
    public void P2_ComputeKey_DistinguishesPaths_IsDeterministic_NeverContainsPath()
    {
        const long size = 4096;
        var mtime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var k1 = CacheStore.ComputeKey("/mnt/a/relatorio.xlsx", size, mtime);
        var k2 = CacheStore.ComputeKey("/mnt/b/relatorio.xlsx", size, mtime);
        var k1again = CacheStore.ComputeKey("/mnt/a/relatorio.xlsx", size, mtime);

        Assert.NotEqual(k1, k2);                 // path participates in identity
        Assert.Equal(k1, k1again);               // same state ⇒ same key
        Assert.DoesNotContain("relatorio", k1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/mnt", k1, StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================
    // CACHE-POISON 3 — Absolute path is NEVER persisted: writes via API
    // and scans raw database bytes (db + wal) searching for path in UTF-8
    // and UTF-16LE. Zero occurrences — AND entry remains retrievable.
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
        Assert.True(raw.Length > 0, "database should exist after Upsert");

        Assert.False(ContainsSequence(raw, Encoding.UTF8.GetBytes(secretPath)),
            "absolute path leaked in UTF-8 in database bytes");
        Assert.False(ContainsSequence(raw, Encoding.Unicode.GetBytes(secretPath)),
            "absolute path leaked in UTF-16LE in database bytes");

        // Even so, identity derives deterministically:
        using var store2 = new CacheStore(_tempDbPath);
        Assert.Equal("abcdef0123456789",
            store2.TryGetByKey(secretPath, size: 12345, mtimeUtc: mtime));
    }

    // ==================================================================
    // CACHE-POISON 4/5/6 — Row poisoned OUTSIDE the API (simulates user/
    // disk malware, surface S2): hash_full swapped, size falsified,
    // algorithm downgraded to MD5 => TryGetByKey THROWS CacheCorruptedException.
    // Poison is never served; corruption is distinguished from absence.
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

        // Legitimate query (original size): row exists but MAC does not match => fail-closed.
        var ex = Assert.Throws<CacheCorruptedException>(() =>
            victim.TryGetByKey(path, size: 2000, mtimeUtc: mtime));
        Assert.Contains("entry_mac", ex.Message);

        // Query with TAMPERED size: derived key differs ⇒ row not
        // found ⇒ null (miss ⇒ recalculation). Poison cannot be served even then.
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
    /// Corruption outside API: direct SQL UPDATE on row whose key derives from
    /// (path,size,mtime) — simulates SQLite file tampering on disk.
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
    // INTEGRITY 1 — Erased SQLite header ⇒ open fails closed
    // (never recycle/recreate database that might contain valid data).
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
            $"expected fail-closed on open; got {ex.GetType().Name}: {ex.Message}");
    }

    // ==================================================================
    // INTEGRITY 2 — Tampered schema (entry_mac column removed) ⇒
    // open fails closed: without integrity column there is no reliable cache.
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
    // INTEGRITY 3 — Incompatible schema_version ⇒ named fail-closed
    // (databases from another schema version are neither opened nor blindly migrated).
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
    // INTEGRITY 4 — Deep page corruption (last third of file inverted)
    // ⇒ open fails closed via structural integrity check.
    // Never opens just because it still seems to work.
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
            $"expected fail-closed on open; got {ex.GetType().Name}: {ex.Message}");
    }

    // ==================================================================
    // OPERATION_ID — deterministic fingerprint of ScanResult CONTENT
    // (card T19: "operation_id = hash of ScanResult content").
    // ==================================================================

    /// <summary>O1 — same content ⇒ same id, regardless of physical order
    /// of lists (canonical order applied before hash — §3).</summary>
    [Fact]
    public void O1_OperationId_IsContentFingerprint_IgnoringPhysicalOrder()
    {
        var id1 = ScanFingerprint.OperationId(SampleScanResult(shuffleMembers: false));
        var id2 = ScanFingerprint.OperationId(SampleScanResult(shuffleMembers: true));

        Assert.False(string.IsNullOrWhiteSpace(id1));
        Assert.Equal(id1, id2);                  // physical order does not decide
        Assert.Matches("^[0-9a-f]{16}$", id1);   // 64 bits truncated, lowercase hex
    }

    /// <summary>O2 — changed content ⇒ changed id (id is a FUNCTION of content).</summary>
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

    /// <summary>O3 — divergent hash ⇒ divergent id (two distinct
    /// classifications never share operation_id).</summary>
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

    /// <summary>Minimal ScanResult: 1 group, 1 identical duplicate (2 members),
    /// 1 real conflict (2 members).</summary>
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

    /// <summary>Reads db + wal (whichever exists) after closing connection.</summary>
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

    /// <summary>Byte sequence search (contiguous subsequence).</summary>
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
