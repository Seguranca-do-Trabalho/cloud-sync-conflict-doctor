namespace Doctor.Tests;

using System.Text.Json;
using Doctor.Core;

/// <summary>
/// T12 (t_d70d59f9) — Minimum DET-03 required by the orchestrator: temporary tree with
/// 2 identical duplicates + 1 real conflict, 3 pipeline runs with fake enumerators in
/// distinct physical orders (forward, reverse, shuffled with fixed seed) ⇒
/// identical byte-for-byte output. The tree uses files > 128 KiB with identical partial
/// windows and different cores so the real conflict REQUIRES Level 3 (full hash)
/// — the proof covers L0→L3 entirely, not just the grouping.
/// </summary>
public sealed class ScanPipelineTests : IDisposable
{
    private const int Kib = 1024;
    private const int LargeFile = 200 * Kib; // > 128 KiB: partial = start+end window
    private const int Window = 64 * Kib;

    private readonly string _root;

    public ScanPipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"t12-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "backup"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup: OS temp will reclaim later
        }
    }

    [Fact]
    public void Scan_ThreeEnumerationOrders_IdenticalByteForByteOutput()
    {
        // ---- tree: identical pair (foto.jpg) + conflicting pair (orcamento) -----------
        var contentA = StandardizedContent(seed: 0xA0);
        var identicalPair1 = CreateFile("docs/foto.jpg", contentA);
        var identicalPair2 = CreateFile("docs/backup/foto.jpg", contentA);

        // Real conflict: SAME size, SAME windows [0,64K)+[end-64K,end), different
        // cores ⇒ partial collision (survives L2) and full hash diverges (L3).
        var conflict1 = CreateFile("docs/orcamento.xlsx", ConflictContent(core: 0x11));
        var conflict2 = CreateFile("docs/orcamento-DESKTOP-ABC123.xlsx", ConflictContent(core: 0x22));

        var entries = new[]
        {
            Entry(identicalPair1),
            Entry(identicalPair2),
            Entry(conflict1),
            Entry(conflict2),
        };

        // ---- three distinct physical orders, all on the SAME tree ------------
        var forward = entries.ToArray();
        var reverse = entries.Reverse().ToArray();
        var shuffled = Shuffle(entries, seed: 42);

        var hasher = new Blake3Hasher();
        var bytesForward = Serialize(RunPipeline(forward, hasher));
        var bytesReverse = Serialize(RunPipeline(reverse, hasher));
        var bytesShuffled = Serialize(RunPipeline(shuffled, hasher));

        Assert.Equal(bytesForward, bytesReverse);
        Assert.Equal(bytesForward, bytesShuffled);

        // ---- correctness of the verdict (not just determinism) ---------------------------
        var result = RunPipeline(forward, hasher);

        Assert.Equal(2, result.Groups.Count);
        Assert.Single(result.IdenticalDuplicates);
        Assert.Single(result.RealConflicts);

        var duplicate = result.IdenticalDuplicates[0];
        Assert.Equal(2, duplicate.Files.Count);
        Assert.Equal(64, duplicate.Hash.Length); // BLAKE3 lowercase hex (ADR-0005 §1)
        Assert.Equal(LargeFile, duplicate.SizeBytes);
        Assert.Equal(identicalPair2, duplicate.Files[0].Path); // canonical order: backup/ < foto
        Assert.Equal(identicalPair1, duplicate.Files[1].Path);

        var conflict = result.RealConflicts[0];
        Assert.Equal("orcamento.xlsx", conflict.NormalizedBaseName);
        Assert.Equal(2, conflict.Files.Count);
        Assert.NotEqual(conflict.Files[0].Hash, conflict.Files[1].Hash); // real divergence
        Assert.Equal(conflict2, conflict.Files[0].Path); // '-' (0x2D) < '.' (0x2E) in bytes
        Assert.Equal(conflict1, conflict.Files[1].Path);
    }

    // ---- tree construction ------------------------------------------------------

    private string CreateFile(string relative, byte[] content)
    {
        var path = Path.Combine(new[] { _root }.Concat(relative.Split('/')).ToArray());
        File.WriteAllBytes(path, content);
        return path;
    }

    private static FileEntry Entry(string path)
    {
        var info = new FileInfo(path);
        return new FileEntry
        {
            Path = path,
            Size = info.Length,
            MtimeUtc = info.LastWriteTimeUtc,
            Attributes = FileAttributes.Normal,
            VolumeId = "t12-volume",
            FileId = path,
        };
    }

    private static byte[] StandardizedContent(byte seed)
    {
        var bytes = new byte[LargeFile];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + (i % 251));
        }

        return bytes;
    }

    /// <summary>Fixed head and tail (guaranteed partial collision); core varies by seed.</summary>
    private static byte[] ConflictContent(byte core)
    {
        var bytes = new byte[LargeFile];

        for (var i = 0; i < Window; i++)
        {
            bytes[i] = (byte)(0xAA + (i % 13));
        }

        for (var i = LargeFile - Window; i < LargeFile; i++)
        {
            bytes[i] = (byte)(0xBB + (i % 17));
        }

        for (var i = Window; i < LargeFile - Window; i++)
        {
            bytes[i] = core;
        }

        return bytes;
    }

    private static FileEntry[] Shuffle(FileEntry[] original, int seed)
    {
        var copy = original.ToArray();
        var random = new Random(seed);

        for (var i = copy.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }

    // ---- pipeline + deterministic serialization ------------------------------------

    private ScanResult RunPipeline(FileEntry[] physicalOrder, IHasher hasher)
    {
        var pipeline = new ScanPipeline(new FakeFileEnumerator(physicalOrder), hasher);
        return pipeline.Run(_root);
    }

    /// <summary>
    /// Canonical serialization of the result for byte-for-byte comparison: the lists
    /// already come out in the pipeline's canonical order; the JSON property order is
    /// the record declaration order (System.Text.Json), fixed and culture-invariant.
    /// </summary>
    private static byte[] Serialize(ScanResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>
    /// Fake enumerator (L0): returns entries EXACTLY in the requested physical order —
    /// forward, reverse, or shuffled. It is not the OrderedFileEnumerator: canonical
    /// ordering is the pipeline's responsibility, and that is what the proof verifies.
    /// </summary>
    private sealed class FakeFileEnumerator : IFileEnumerator
    {
        private readonly IReadOnlyList<FileEntry> _order;

        public FakeFileEnumerator(IReadOnlyList<FileEntry> order) => _order = order;

        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) =>
            new(_order, Array.Empty<ScanError>(), new ScanTelemetry());
    }
}
