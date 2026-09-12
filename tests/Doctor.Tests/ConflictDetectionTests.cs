namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// CD-01 (t_57b34881) — HSH-02, HSH-03, and HSH-04 from test-strategy (§3.6).
///
/// HSH-02 FullHash_ExecutedOnlyForPartialSurvivors:
///   Tree with same-size/distinct-content pairs (eliminated in L2) alongside
///   pair with guaranteed partial collision (fixed head+tail, distinct core);
///   CountingHasher proves that file eliminated in L2 NEVER receives FullHash and that
///   survivor receives exactly 1 full hash per member.
///
/// HSH-03 IdenticalContent_FullHashEqual_ClassifiedIdenticalDuplicate:
///   Byte-identical copies -> exactly 1 IdenticalDuplicate, zero RealConflicts,
///   BLAKE3 hex 64 lowercase hash, canonical order in Files.
///
/// HSH-04 DifferentContent_SameNormalizedBase_ClassifiedRealConflict:
///   Same normalized name, distinct content -> RealConflict covering ALL
///   members with individual hash; real conflict NEVER appears in IdenticalDuplicates
///   (Q9/Q10 of SPEC §8).
/// </summary>
public sealed class ConflictDetectionTests : IDisposable
{
    private const int Kib = 1024;
    private const int LargeFileSize = 200 * Kib; // > 128 KiB: distinct partial windows
    private const int WindowSize = 64 * Kib;

    private readonly string _root;
    private readonly List<string> _tempFiles = new();

    public ConflictDetectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cd01-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ================================================================
    // HSH-02: FullHash executed ONLY for Level 2 survivors
    // ================================================================

    [Fact]
    public void FullHash_ExecutedOnlyForL2Survivors_NoCallOnEliminated()
    {
        // Tree with:
        //   - Same-size/distinct-content pair (DIFFERENT partials -> eliminated in L2)
        //   - Partial collision pair (fixed head+tail, distinct core -> survives L2)
        var distinctContentA = StandardContent(seed: 0xA1);
        var distinctContentB = StandardContent(seed: 0xB2); // same size, different bytes
        var collisionContentA = ContentWithPartialCollision(core: 0x11);
        var collisionContentB = ContentWithPartialCollision(core: 0x22);

        var distinctPathA = CreateFile("folder/distinctA.bin", distinctContentA);
        var distinctPathB = CreateFile("folder/distinctB.bin", distinctContentB);
        var collisionPathA = CreateFile("conflict/file.xlsx", collisionContentA);
        var collisionPathB = CreateFile("conflict/file-DESKTOP-ABC123.xlsx", collisionContentB);

        var entries = new[]
        {
            Entry(distinctPathA),
            Entry(distinctPathB),
            Entry(collisionPathA),
            Entry(collisionPathB),
        };

        var counting = new CountingHasher();
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), counting);
        var result = pipeline.Run(_root);

        // ---- expected verdict -----------------------------------------------
        // Distinct pair: different partial -> NOT survivor in L2 -> does NOT enter identical or conflicts
        // Collision pair: equal partial -> survives L2 -> full hash computed -> result
        Assert.Empty(result.IdenticalDuplicates); // no identical copy
        Assert.Single(result.RealConflicts);     // only collision pair
        Assert.Single(result.Groups);           // one group (normalized base + equal sizes)

        // ---- proof via CountingHasher ----------------------------------------
        var distinctAEntry = entries.Single(e => e.Path == distinctPathA);
        var distinctBEntry = entries.Single(e => e.Path == distinctPathB);
        var collisionAEntry = entries.Single(e => e.Path == collisionPathA);
        var collisionBEntry = entries.Single(e => e.Path == collisionPathB);

        // Files eliminated in L2 NEVER received FullHash
        Assert.DoesNotContain(distinctPathA, counting.FullHashPaths);
        Assert.DoesNotContain(distinctPathB, counting.FullHashPaths);

        // L2 survivors received exactly 1 FullHash each
        Assert.Contains(collisionPathA, counting.FullHashPaths);
        Assert.Contains(collisionPathB, counting.FullHashPaths);
        Assert.Equal(2, counting.FullHashPaths.Count);

        // All members of surviving group have FullHash called once
        Assert.Equal(1, counting.FullHashCalls[collisionPathA]);
        Assert.Equal(1, counting.FullHashCalls[collisionPathB]);
    }

    // ================================================================
    // HSH-03: IdenticalContent -> IdenticalDuplicate
    // ================================================================

    [Fact]
    public void IdenticalContent_FullHashEqual_ClassifiedAsIdenticalDuplicate()
    {
        var content = new byte[50 * Kib];
        (new Random(77)).NextBytes(content);

        // Same normalized base: "a (1).txt" and "a (2).txt" normalize to "a.txt"
        // => group by base + size; identical content => IdenticalDuplicate.
        var c1 = CreateFile("dir/a (1).txt", content);
        var c2 = CreateFile("dir/a (2).txt", content);
        var c3 = CreateFile("other/a (1).txt", content); // third copied

        var entries = new[] { Entry(c1), Entry(c2), Entry(c3) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // exactly 1 IdenticalDuplicate
        Assert.Single(result.IdenticalDuplicates);
        Assert.Empty(result.RealConflicts);

        var dup = result.IdenticalDuplicates[0];

        // BLAKE3 hex 64 lowercase hash
        Assert.Matches(@"^[0-9a-f]{64}$", dup.Hash);

        // common size
        Assert.Equal(content.Length, dup.SizeBytes);

        // 3 files in list, canonical order by UTF-8 bytes
        // dir/a (1).txt < dir/a (2).txt < other/a (1).txt (byte order)
        Assert.Equal(3, dup.Files.Count);
        Assert.Equal(c1, dup.Files[0].Path);
        Assert.Equal(c2, dup.Files[1].Path);
        Assert.Equal(c3, dup.Files[2].Path);
    }

    // ================================================================
    // HSH-04: DifferentContent_SameNormalizedBase -> RealConflict
    // ================================================================

    [Fact]
    public void DifferentContent_SameNormalizedBase_ClassifiedAsRealConflict()
    {
        // Fixed head+tail (guaranteed partial collision), distinct cores.
        // Size > 128 KiB so partial covers only windows and full covers all.
        var contentA = ContentWithPartialCollision(core: 0x11);
        var contentB = ContentWithPartialCollision(core: 0x22);

        // "budget-DESKTOP-ABC123.xlsx" normalizes to "budget.xlsx" (valid 6-char hostname).
        var c1 = CreateFile("project/budget.xlsx", contentA);
        var c2 = CreateFile("project/budget-DESKTOP-ABC123.xlsx", contentB);

        var entries = new[] { Entry(c1), Entry(c2) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // exactly 1 RealConflict
        Assert.Empty(result.IdenticalDuplicates);
        Assert.Single(result.RealConflicts);

        var conflict = result.RealConflicts[0];

        // identical normalized name
        Assert.Equal("budget.xlsx", conflict.NormalizedBaseName);
        Assert.Equal(contentA.Length, conflict.SizeBytes);

        // all members covered with individual hash
        Assert.Equal(2, conflict.Files.Count);
        var memberA = conflict.Files.First(f => f.Path == c1);
        var memberB = conflict.Files.First(f => f.Path == c2);
        Assert.Matches(@"^[0-9a-f]{64}$", memberA.Hash);
        Assert.Matches(@"^[0-9a-f]{64}$", memberB.Hash);
        Assert.NotEqual(memberA.Hash, memberB.Hash); // distinct individual hashes

        // real conflict NEVER appears in IdenticalDuplicates (Q9/Q10 of SPEC §8)
        foreach (var dup in result.IdenticalDuplicates)
        {
            Assert.DoesNotContain(c1, dup.Files.Select(f => f.Path));
            Assert.DoesNotContain(c2, dup.Files.Select(f => f.Path));
        }
    }

    // ================================================================
    // CD-02 (t_a77122c3) — Mutual exclusion contract of schema v1 §6.1/§6.2:
    // group classified as IdenticalDuplicate NEVER generates entry in
    // RealConflicts and vice-versa; classification is PER GROUP.
    // ================================================================

    // ----------------------------------------------------------------
    // Case 1 — Mixed group: 2 members identical to each other + 1 divergent.
    // Schema §6.2: real_conflicts item covers ALL members, including internally
    // identical subset; §6.1: duplicate only forms from group whose full hashes
    // are ALL equal. The internal subset does NOT become IdenticalDuplicate —
    // the consumer reconstructs subgroups by per-file hashes.
    // ----------------------------------------------------------------
    [Fact]
    public void MixedGroup_InternallyIdenticalSubset_DoesNotBecomeIdenticalDuplicate_BecomesSingleRealConflict()
    {
        // Three members, same normalized base ("report.xlsx") and same size:
        //   A = report.xlsx            (content X)
        //   A = report (1).xlsx        (content X — identical to A)
        //   B = report-DESKTOP-ABC123.xlsx (content Y — divergent)
        // Fixed head+tail guarantees partial collision (survives L2);
        // distinct cores guarantee distinct full hashes (X != Y).
        var contentX = ContentWithPartialCollision(core: 0x11);
        var contentY = ContentWithPartialCollision(core: 0x22);

        var pathA1 = CreateFile("finance/report.xlsx", contentX);
        var pathA2 = CreateFile("finance/report (1).xlsx", contentX);
        var pathB = CreateFile("backup/report-DESKTOP-ABC123.xlsx", contentY);

        var entries = new[] { Entry(pathA1), Entry(pathA2), Entry(pathB) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // SINGLE entry in RealConflicts covering all 3 members.
        Assert.Single(result.RealConflicts);
        var conflict = result.RealConflicts[0];
        Assert.Equal("report.xlsx", conflict.NormalizedBaseName);
        Assert.Equal(contentX.Length, conflict.SizeBytes);
        Assert.Equal(3, conflict.Files.Count);
        Assert.Equal(
            new[] { pathA1, pathA2, pathB }.OrderBy(p => p, StringComparer.Ordinal),
            conflict.Files.Select(f => f.Path));

        // ZERO entries in IdenticalDuplicates: internally identical pair does NOT become duplicate.
        Assert.Empty(result.IdenticalDuplicates);

        // Reconstructibility of subset via per-file hashes (schema §6.2):
        // exactly two hash values, identical pair shares one of them.
        var hashA1 = conflict.Files.First(f => f.Path == pathA1).Hash;
        var hashA2 = conflict.Files.First(f => f.Path == pathA2).Hash;
        var hashB = conflict.Files.First(f => f.Path == pathB).Hash;
        Assert.Equal(hashA1, hashA2);
        Assert.NotEqual(hashA1, hashB);
    }

    // ----------------------------------------------------------------
    // Case 2 — Sweep property: for any ScanResult, every
    // normalized_base_name appears in at most one of the two lists
    // (schema §6.2, final paragraph). Composite tree exercising all
    // classification outcomes in the same result: mixed group
    // (RealConflict), identical trio (IdenticalDuplicate), pair eliminated in L2
    // (present only in Groups — audit trail §6.0) and single file.
    // ----------------------------------------------------------------
    [Fact]
    public void Property_NormalizedBaseName_AppearsInAtMostOneOfTwoLists_ForAnyScanResult()
    {
        // Mixed group -> RealConflict.
        var mixedX = ContentWithPartialCollision(core: 0x11);
        var mixedY = ContentWithPartialCollision(core: 0x22);
        var m1 = CreateFile("finance/report.xlsx", mixedX);
        var m2 = CreateFile("finance/report (1).xlsx", mixedX);
        var m3 = CreateFile("backup/report-DESKTOP-ABC123.xlsx", mixedY);

        // Byte-identical trio -> IdenticalDuplicate.
        var identical = new byte[30 * Kib];
        for (var i = 0; i < identical.Length; i++)
        {
            identical[i] = (byte)(i % 253);
        }
        var d1 = CreateFile("docs/notes.txt", identical);
        var d2 = CreateFile("docs/notes (1).txt", identical);
        var d3 = CreateFile("docs/notes (2).txt", identical);

        // Same-size/distinct-content pair -> eliminated in L2 (no partial collision),
        // remains only in Groups (audit trail), outside both final lists.
        var deadA = CreateFile("tmp/dead.bin", StandardContent(seed: 0x01));
        var deadB = CreateFile("tmp/dead-DESKTOP-ABC123.bin", StandardContent(seed: 0x02));

        // Single file -> never a candidate.
        var solo = CreateFile("root/solo.txt", [0x53, 0x4F, 0x4C, 0x4F]);

        var entries = new[] { Entry(m1), Entry(m2), Entry(m3), Entry(d1), Entry(d2), Entry(d3), Entry(deadA), Entry(deadB), Entry(solo) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // Scenario prepared as expected: 3 candidate groups, 1 for each verdict.
        Assert.Equal(3, result.Groups.Count);
        Assert.Single(result.IdenticalDuplicates);
        Assert.Single(result.RealConflicts);

        // PROPERTY: empty intersection between normalized names of both lists.
        var namesInConflicts = result.RealConflicts
            .Select(c => c.NormalizedBaseName)
            .ToHashSet(StringComparer.Ordinal);
        var namesInDuplicates = result.IdenticalDuplicates
            .SelectMany(d => d.Files.Select(f => Grouping.NormalizeBaseName(Path.GetFileName(f.Path))))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(namesInConflicts.Intersect(namesInDuplicates));

        // Concrete reinforcement: dead group in L2 ("dead.bin") exists in Groups
        // (audit trail) but appears in NEITHER list — zero
        // also satisfies "at most one".
        Assert.Contains(result.Groups, g => g.NormalizedBaseName == "dead.bin");
        Assert.DoesNotContain("dead.bin", namesInConflicts);
        Assert.DoesNotContain("dead.bin", namesInDuplicates);

        // And each list contains exactly the name of its own verdict.
        Assert.Equal(new[] { "report.xlsx" }, namesInConflicts);
        Assert.Equal(new[] { "notes.txt" }, namesInDuplicates);
    }

    // ----------------------------------------------------------------
    // Case 3 — Single pair of group survives L2 (partial collision) but
    // distinct full hashes: ONLY RealConflict, ZERO IdenticalDuplicates
    // (schema §6.2: at least two distinct hashes after L2 => real conflict).
    // ----------------------------------------------------------------
    [Fact]
    public void PairSurvivesL2_WithDistinctFullHashes_OnlyRealConflict_ZeroIdenticalDuplicates()
    {
        var contentA = ContentWithPartialCollision(core: 0x33);
        var contentB = ContentWithPartialCollision(core: 0x44);

        var pathA = CreateFile("contracts/agreement.xlsx", contentA);
        var pathB = CreateFile("contracts/agreement (1).xlsx", contentB);

        var entries = new[] { Entry(pathA), Entry(pathB) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // Group survived L2 and became conflict — nothing in duplicates list.
        Assert.Single(result.RealConflicts);
        Assert.Empty(result.IdenticalDuplicates);

        var conflict = result.RealConflicts[0];
        Assert.Equal("agreement.xlsx", conflict.NormalizedBaseName);
        Assert.Equal(2, conflict.Files.Count);

        var hashA = conflict.Files.First(f => f.Path == pathA).Hash;
        var hashB = conflict.Files.First(f => f.Path == pathB).Hash;
        Assert.Matches(@"^[0-9a-f]{64}$", hashA);
        Assert.Matches(@"^[0-9a-f]{64}$", hashB);
        Assert.NotEqual(hashA, hashB);
    }

    // ----------------------------------------------------------------
    // Case 4 — Byte-identical trio: SINGLE IdenticalDuplicate with 3
    // Files and ZERO RealConflicts (schema §6.1: one element per class of
    // full content equivalence, with 2 or more files).
    // ----------------------------------------------------------------
    [Fact]
    public void ByteIdenticalTrio_SingleIdenticalDuplicate_WithThreeFiles_ZeroRealConflicts()
    {
        var content = new byte[12 * Kib];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(0xC3 ^ (i % 31));
        }

        // Bases that all normalize to "config.ini".
        var c1 = CreateFile("app/config.ini", content);
        var c2 = CreateFile("app/config (1).ini", content);
        var c3 = CreateFile("bak/config-DESKTOP-ZZZ999.ini", content);

        var entries = new[] { Entry(c1), Entry(c2), Entry(c3) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // SINGLE entry, three files, zero real conflicts.
        Assert.Single(result.IdenticalDuplicates);
        Assert.Empty(result.RealConflicts);

        var dup = result.IdenticalDuplicates[0];
        Assert.Matches(@"^[0-9a-f]{64}$", dup.Hash);
        Assert.Equal(content.Length, dup.SizeBytes);
        Assert.Equal(3, dup.Files.Count);
        Assert.Equal(
            new[] { c1, c2, c3 }.OrderBy(p => p, StringComparer.Ordinal),
            dup.Files.Select(f => f.Path));
    }

    // ---- helpers ----------------------------------------------------------

    private string CreateFile(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private static FileEntry Entry(string path) => new()
    {
        Path = path,
        Size = new FileInfo(path).Length,
        MtimeUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        VolumeId = "cd01-vol",
        FileId = path,
    };

    private static byte[] StandardContent(byte seed)
    {
        var bytes = new byte[LargeFileSize];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + (i % 251));
        }
        return bytes;
    }

    /// <summary>Fixed head and tail (guaranteed partial collision); core varies by seed.</summary>
    private static byte[] ContentWithPartialCollision(byte core)
    {
        var bytes = new byte[LargeFileSize];
        for (var i = 0; i < WindowSize; i++)
        {
            bytes[i] = (byte)(0xAA + (i % 13));
        }
        for (var i = LargeFileSize - WindowSize; i < LargeFileSize; i++)
        {
            bytes[i] = (byte)(0xBB + (i % 17));
        }
        for (var i = WindowSize; i < LargeFileSize - WindowSize; i++)
        {
            bytes[i] = core;
        }
        return bytes;
    }

    private sealed class FakeFileEnumerator : IFileEnumerator
    {
        private readonly IReadOnlyList<FileEntry> _order;
        public FakeFileEnumerator(IReadOnlyList<FileEntry> order) => _order = order;
        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) =>
            new(_order, Array.Empty<ScanError>(), new ScanTelemetry());
    }

    /// <summary>
    /// Spy hasher that delegates to a real hasher but records all calls.
    /// </summary>
    private sealed class CountingHasher : IHasher
    {
        private readonly Blake3Hasher _real = new();

        public List<string> FullHashPaths { get; } = new();
        public Dictionary<string, int> FullHashCalls { get; } = new();

        public string PartialHash(FileEntry entry, CancellationToken ct = default) =>
            _real.PartialHash(entry, ct);

        public string FullHash(FileEntry entry, CancellationToken ct = default)
        {
            FullHashPaths.Add(entry.Path);
            if (!FullHashCalls.TryAdd(entry.Path, 1))
            {
                FullHashCalls[entry.Path]++;
            }
            return _real.FullHash(entry, ct);
        }
    }
}
