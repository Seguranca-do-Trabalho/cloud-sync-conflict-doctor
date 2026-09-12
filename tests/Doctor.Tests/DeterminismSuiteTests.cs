namespace Doctor.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Doctor.Core;

/// <summary>
/// T21 (t_1b602b7b) — Complete determinism suite DET-01..DET-06
/// (docs/test-strategy.md §3.1; SPEC §20; ADR-0003/0004; GATE 2).
///
/// Standard tree for this suite (all families use the SAME composition required
/// by the card): 50 files = 25 paired identical (11 pairs + 1 trio),
/// 10 real conflicts (5 groups × 2 copies with same partial windows and different
/// cores) and 15 uniques. Contents, sizes, names and mtimes derived ONLY
/// from indices — zero randomness outside declared seeded permutations.
///
/// Byte-by-byte comparison layers in integration tests:
/// 1. Canonical serialization of ScanResult (T12 pattern); and
/// 2. Full schema v1 report via ReportWriterJson with frozen timestamps
///    (condition §2.3) and paths relative to root (condition §2.4).
///
/// Documented deviations from the §3.1 table:
/// - DET-03: the product does NOT have a parallel hash pool — the decision is serial by
///   construction (ADR-0004 rules 1-2; ScanPipeline). The equivalent proof required
///   by the gate is that THREAD COMPLETION ORDER does not alter output: N concurrent
///   scans (ThreadPool, distinct shuffled inputs) must produce bytes identical to
///   the serial scan. Any mutable shared state would break this test.
/// - DET-06: the generated_from.root_path field is absolute per schema and varies per
///   machine; the "&lt;ROOT&gt;" mask replaces ONLY this value before comparison.
///   Everything else in the report is compared byte-for-byte against the committed
///   golden. Regeneration: env DOCTOR_REGEN_GOLDENS=1
///   (dotnet test --filter FullyQualifiedName~DeterminismSuiteTests).
/// </summary>
[Trait("Category", "Determinism")]
public sealed class DeterminismSuiteTests : IDisposable
{
    private const int Kib = 1024;

    /// <summary>Large size (&gt; 128 KiB): partial = start+end windows (ADR-0005 §3).</summary>
    private const int LargeFile = 200 * Kib;
    private const int Window = 64 * Kib;

    private static readonly DateTimeOffset FrozenStart = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FrozenEnd = new(2026, 1, 1, 12, 0, 1, TimeSpan.Zero);

    /// <summary>DET-02: 10 FIXED seeds declared in code (§3.1 — no randomness).</summary>
    private static readonly int[] FixedSeeds =
    [
        1, 7, 42, 255, 1024, 40961, 123456, 987654321, 1999999999, 2147483646,
    ];

    private readonly string _root;

    public DeterminismSuiteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"t21-det-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup: OS temp reclaims later
        }
    }

    // =====================================================================================
    // DET-01 — three enumeration orders ⇒ byte-identical reports
    // =====================================================================================

    [Fact]
    public void Scan_ThreeEnumerationOrders_ReportsByteIdentical()
    {
        var tree = CreateStandardTree();
        var entries = tree.Entries; // creation order = "real" physical order

        var orders = new (string Label, FileEntry[] Order)[]
        {
            ("forward", entries.ToArray()),
            ("reverse", entries.Reverse().ToArray()),
            ("shuffled-seed-42", Shuffle(entries, seed: 42)),
        };

        // Proof sanity: the three physical orders must be DIFFERENT from each other,
        // otherwise the test would be vacuous.
        Assert.NotEqual(orders[0].Order.Select(e => e.Path), orders[1].Order.Select(e => e.Path));
        Assert.NotEqual(orders[0].Order.Select(e => e.Path), orders[2].Order.Select(e => e.Path));

        var hasher = new Blake3Hasher();

        var reference = RunTwoLayerScan(tree.Root, orders[0].Order, hasher);

        foreach (var (_, order) in orders.Skip(1))
        {
            var candidate = RunTwoLayerScan(tree.Root, order, hasher);

            AssertBytesIdentical(
                reference.CanonicalResult,
                candidate.CanonicalResult,
                $"DET-01 ScanResult: order '{orders[0].Label}' vs others");
            AssertBytesIdentical(
                reference.ReportV1,
                candidate.ReportV1,
                $"DET-01 report-v1: order '{orders[0].Label}' vs others");
        }

        // ---- correctness of the verdict (not just determinism) ---------------------------
        var result = RunPipeline(orders[0].Order, hasher).Run(tree.Root);

        // T21 diagnostics: observed vs expected composition (fails with real counts).
        Assert.True(
            result.IdenticalDuplicates.Count == 12 && result.RealConflicts.Count == 5,
            "unexpected composition: identical=" + result.IdenticalDuplicates.Count +
            " conflicts=" + result.RealConflicts.Count +
            " groups=" + result.Groups.Count +
            " | dups: " + string.Join(";", result.IdenticalDuplicates.Select(d => d.Files.Count + "x@" + d.SizeBytes)) +
            " | conflicts: " + string.Join(";", result.RealConflicts.Select(c => c.NormalizedBaseName)));

        // Candidates: 11 pairs + 1 trio + 5 conflicts = 17 groups; verdicts:
        // 12 identical duplicates and 5 real conflicts; uniques die at L1.
        Assert.Equal(17, result.Groups.Count);
        Assert.Equal(12, result.IdenticalDuplicates.Count);
        Assert.Equal(5, result.RealConflicts.Count);
        Assert.Equal(25, result.IdenticalDuplicates.Sum(d => d.Files.Count));
        Assert.Equal(10, result.RealConflicts.Sum(c => c.Files.Count));

        // Canonical order within lists: first group is the pair in backup/
        // ('b' 0x62 precedes 'd' 0x64 of dups/ and 't' 0x74 of trio/).
        var firstDup = result.IdenticalDuplicates[0];
        Assert.Equal(2, firstDup.Files.Count);
        Assert.EndsWith("backup/p00/foto-p00.jpg", firstDup.Files[0].Path, StringComparison.Ordinal);
        Assert.Equal(64, firstDup.Hash.Length); // BLAKE3 lowercase hex (ADR-0005 §1)

        // First conflict: member in conflicts/mirror/ precedes conflicts/
        // ('m' 0x6D < 'o' 0x6F).
        var firstConflict = result.RealConflicts[0];
        Assert.Equal("orc-k00.bin", firstConflict.NormalizedBaseName);
        Assert.Contains("conflicts/mirror/", firstConflict.Files[0].Path, StringComparison.Ordinal);
        Assert.NotEqual(firstConflict.Files[0].Hash, firstConflict.Files[1].Hash);

        // Security invariant echoed in projected telemetry (SPEC §21).
        Assert.Equal(0, reference.Telemetry.PlaceholderBytesRead);
    }

    // =====================================================================================
    // DET-02 — ten fixed shuffle seeds ⇒ all identical to forward order
    // =====================================================================================

    [Fact]
    public void Scan_TenFixedShuffleSeeds_AllReportsByteIdentical()
    {
        var tree = CreateStandardTree();
        var hasher = new Blake3Hasher();

        var reference = RunTwoLayerScan(tree.Root, tree.Entries.ToArray(), hasher);

        foreach (var seed in FixedSeeds)
        {
            var permuted = Shuffle(tree.Entries, seed);

            // Sanity: permutation with this seed CANNOT coincide with forward order.
            Assert.NotEqual(tree.Entries.Select(e => e.Path), permuted.Select(e => e.Path));

            var candidate = RunTwoLayerScan(tree.Root, permuted, hasher);

            AssertBytesIdentical(
                reference.CanonicalResult,
                candidate.CanonicalResult,
                $"DET-02 ScanResult: seed {seed}");
            AssertBytesIdentical(
                reference.ReportV1,
                candidate.ReportV1,
                $"DET-02 report-v1: seed {seed}");
        }
    }

    // =====================================================================================
    // DET-03 — thread completion order does not alter output
    // =====================================================================================

    /// <summary>
    /// §11/§20 proof adapted to the real product (see class doc): the pipeline decides
    /// serially over canonical collections; what the gate requires is that external
    /// concurrency (N simultaneous scans finishing in arbitrary order, each with a
    /// different physical permutation) does NOT produce a single byte different from
    /// the serial reference scan (DET-01). Mutable static state or arrival-order
    /// decisions would fail here intermittently.
    /// </summary>
    [Fact]
    public void ParallelHashing_ThreadCompletionOrder_ReportBytesIdentical()
    {
        var tree = CreateStandardTree();
        var hasher = new Blake3Hasher();

        var serial = RunTwoLayerScan(tree.Root, tree.Entries.ToArray(), hasher);

        const int workers = 12;
        var results = new byte[workers][];

        Parallel.For(0, workers, w =>
        {
            var permuted = Shuffle(tree.Entries, seed: (w * 13) + 5);
            results[w] = RunTwoLayerScan(tree.Root, permuted, hasher).ReportV1;
        });

        for (var w = 0; w < workers; w++)
        {
            AssertBytesIdentical(
                serial.ReportV1,
                results[w],
                $"DET-03 report-v1: worker {w} (finished at position {w}, arbitrary completion order)");
        }
    }

    // =====================================================================================
    // DET-04 — canonical order = UTF-8 bytes, culture-immune
    // =====================================================================================

    /// <summary>
    /// Unit (§3.1): PathOrder.Comparer on the universe of 50 entries of this suite
    /// (including the five trap names Zebra/apple/Apple/zebra/Árvore) with
    /// CurrentCulture forced to tr-TR and pt-BR. Independent oracle: UTF-8 byte
    /// lexicographic comparison implemented HERE, outside the product. The resulting
    /// order must be EQUAL to the oracle in both cultures, and the relative position
    /// of the five trap names is the explicit expectation (uppercase &lt; lowercase &lt;
    /// accented: 0x41 &lt; 0x5A &lt; 0x61 &lt; 0x7A &lt; 0xC3).
    /// </summary>
    [Fact]
    public void PathOrdering_UsesUtf8ByteOrder_RegardlessOfCulture()
    {
        var universe = UniverseInMemory(); // 50 entries, no disk (unit)

        var expected = universe
            .OrderBy(e => e.Path, Comparer<string>.Create(CompareByUtf8Bytes))
            .Select(e => e.Path)
            .ToArray();

        // EXPLICIT relative position of the five trap names (table §3.1).
        var traps = new[] { "Apple.txt", "Zebra.txt", "apple.txt", "zebra.txt", "Árvore.txt" };
        var positions = traps
            .Select(name => Array.FindIndex(expected, p => p.EndsWith("/" + name, StringComparison.Ordinal)))
            .ToArray();
        Assert.All(positions, p => Assert.True(p >= 0, $"trap name absent from universe: index {p}"));
        Assert.True(positions.SequenceEqual(positions.OrderBy(p => p).ToArray()),
            "expected UTF-8 order among traps violated in oracle");

        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { "tr-TR", "pt-BR" })
            {
                var ci = new CultureInfo(culture);
                CultureInfo.CurrentCulture = ci;
                CultureInfo.CurrentUICulture = ci;

                // Shuffled input (seed 77) in a FRESH copy per culture.
                var input = Shuffle(universe, seed: 77);
                var obtained = input
                    .OrderBy(e => e, PathOrder.Comparer)
                    .Select(e => e.Path)
                    .ToArray();

                Assert.Equal(
                    expected,
                    obtained,
                    StringComparer.Ordinal);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    // =====================================================================================
    // DET-05 — tie-break mtime → size → path; never first-seen
    // =====================================================================================

    /// <summary>
    /// Unit (§3.1): resolver (§17) with constructed ties. Each scenario is executed
    /// with input in forward, reverse and shuffled orders (fixed seeds); winner and
    /// sacrifice sequence must be IDENTICAL across all presentations. In every
    /// scenario the "forward" presentation places a LOSER in first position — a
    /// first-seen resolver would pick the wrong winner.
    ///
    /// Contract note: the grouper (Level 1) only produces groups of uniform size; the
    /// mtime→size→path chain exists as defense-in-depth in the resolver and is
    /// exercised here with directly constructed groups (ConflictGroup is public
    /// contract — docs/test-strategy.md §3.1 DET-05 asks for exactly these ties).
    /// </summary>
    [Fact]
    public void TieBreak_MtimeThenSizeThenPath_NeverFirstSeen()
    {
        var orders = new[] { 0, -1, 11, 23 }; // 0=forward, -1=reverse, others=shuffle

        // ---- Scenario A: same mtime → SIZE decides (keep-newest) -------------------
        // First element of forward presentation is the SMALLEST file (obvious loser).
        var groupA = GroupWithMembers(
            mtimeIndex: 10,
            ("/g/a-smaller.docx", 100L),
            ("/g/c-medium.docx", 200L),
            ("/g/b-larger.docx", 300L));
        var winnerA = "/g/b-larger.docx";
        VerifyStability(groupA, new KeepNewest(), winnerA, orders, "A keep-newest");

        // ---- Scenario B: keep-largest ignores more recent mtime of third parties -------
        var groupB = GroupWithMembers(
            mtimeIndex: 20,
            ("/h/recently-modified.docx", 111L),
            ("/h/z-small.docx", 999L),
            ("/h/a-medium.docx", 500L));
        var winnerB = "/h/z-small.docx"; // larger size wins even with less recent mtime
        VerifyStability(groupB, new KeepLargest(), winnerB, orders, "B keep-largest");

        // ---- Scenario C: same mtime + size → PATH decides (real pair from universe) --
        // Replica of the photo-p00 pair (same mtime, same size): smaller path wins.
        var groupC = GroupWithMembers(
            mtimeIndex: 30,
            ("/tree/backup/p00/foto-p00.jpg", 64L * Kib),
            ("/tree/dups/p00/foto-p00.jpg", 64L * Kib));
        var winnerC = "/tree/backup/p00/foto-p00.jpg"; // 'b' < 'd'
        VerifyStability(groupC, new KeepNewest(), winnerC, orders, "C absolute tie");

        // ---- Scenario D: keep-machine among representatives of the SAME machine --------
        var groupD = GroupWithMembers(
            mtimeIndex: 40,
            ("/i/doc-DESKTOP-HOSTZZZZZ.xlsx", 400L),
            ("/i/doc-DESKTOP-HOSTAAAAA.xlsx", 400L),
            ("/i/other-host/doc-DESKTOP-OTHER999.xlsx", 400L));
        var winnerD = "/i/doc-DESKTOP-HOSTAAAAA.xlsx"; // smaller path AMONG representatives
        VerifyStability(groupD, new KeepMachine("HOSTAAAAA"), winnerD, orders, "D keep-machine");

        // ---- Scenario E: keep-manual — explicit choice, stable plan -------------
        var groupE = GroupWithMembers(
            mtimeIndex: 50,
            ("/j/one.txt", 10L),
            ("/j/two.txt", 10L),
            ("/j/three.txt", 10L));
        VerifyStablePlan(groupE, new KeepManual("/j/two.txt"), orders, "E keep-manual");
    }

    private static void VerifyStability(
        ConflictGroup group,
        ResolutionStrategy strategy,
        string expectedWinner,
        int[] seeds,
        string scenario)
    {
        string? seenWinner = null;
        string[]? seenSacrifices = null;
        string? expectedReason = null;

        foreach (var seed in seeds)
        {
            var presentation = Present(group.Members, seed);
            var reorderedGroup = new ConflictGroup(group.NormalizedBaseName, group.SizeBytes, presentation);

            // Dispatch by concrete type (specific overloads of Resolution.Resolve).
            var plan = strategy switch
            {
                KeepNewest => Resolution.Resolve(reorderedGroup, (KeepNewest)strategy),
                KeepLargest => Resolution.Resolve(reorderedGroup, (KeepLargest)strategy),
                KeepMachine machine => Resolution.Resolve(reorderedGroup, machine),
                _ => throw new InvalidOperationException($"strategy without dispatch: {scenario}"),
            };

            Assert.Equal(expectedWinner, plan.Winner.Path);

            if (seenWinner is null)
            {
                seenWinner = plan.Winner.Path;
                seenSacrifices = plan.Sacrifices.Select(s => s.Entry.Path).ToArray();
            }
            else
            {
                Assert.Equal(seenWinner, plan.Winner.Path);
                Assert.Equal(
                    seenSacrifices,
                    plan.Sacrifices.Select(s => s.Entry.Path).ToArray());
            }

            // Sacrifices always in canonical path order (ADR-0003).
            var sacrifices = plan.Sacrifices.Select(s => s.Entry.Path).ToArray();
            var sorted = sacrifices.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.Equal(sorted, sacrifices);

            // Auditable reason consistent with the strategy (computed once).
            expectedReason ??= ExpectedReason(strategy);
            Assert.All(plan.Sacrifices, s => Assert.Equal(expectedReason, s.Reason));
        }
    }

    private static void VerifyStablePlan(
        ConflictGroup group,
        KeepManual strategy,
        int[] seeds,
        string scenario)
    {
        string? signature = null;

        foreach (var seed in seeds)
        {
            var presentation = Present(group.Members, seed);
            var reorderedGroup = new ConflictGroup(group.NormalizedBaseName, group.SizeBytes, presentation);

            var plan = Resolution.Resolve(reorderedGroup, strategy);

            Assert.Equal(strategy.ChoicePath, plan.Winner.Path);

            var current = plan.Winner.Path + "|" +
                        string.Join(";", plan.Sacrifices.Select(s => s.Entry.Path + ":" + s.Reason));

            signature ??= current;
            Assert.Equal(signature, current);
        }
    }

    private static IReadOnlyList<FileEntry> Present(IReadOnlyList<FileEntry> members, int seed) =>
        seed switch
        {
            0 => members.ToArray(),
            -1 => members.Reverse().ToArray(),
            _ => Shuffle(members, seed),
        };

    private static string ExpectedReason(ResolutionStrategy strategy) => strategy switch
    {
        KeepNewest => "keep-newest",
        KeepLargest => "keep-largest",
        KeepMachine k => $"keep-machine:{k.MachineId}",
        KeepManual => "keep-manual",
        _ => throw new InvalidOperationException("unexpected strategy"),
    };

    /// <summary>
    /// Constructs group with synthetic FileEntries: mtime DERIVED from index (fixed per
    /// scenario, explicit UTC — fixture rule §5), sizes per the pairs.
    /// </summary>
    private static ConflictGroup GroupWithMembers(
        int mtimeIndex,
        params (string Path, long Size)[] specs)
    {
        var mtime = new DateTimeOffset(2026, 1, 1, 0, mtimeIndex, 0, TimeSpan.Zero);

        var members = specs
            .Select(spec => new FileEntry
            {
                Path = spec.Path,
                Size = spec.Size,
                MtimeUtc = mtime,
                Attributes = FileAttributes.Normal,
                VolumeId = "det-tie",
                FileId = spec.Path,
            })
            .ToArray();

        var baseName = Path.GetFileName(specs[0].Path);

        return new ConflictGroup(baseName, specs[0].Size, members);
    }

    // =====================================================================================
    // DET-06 — committed golden file
    // =====================================================================================

    [Fact]
    public void GoldenReport_FixtureMin_MatchesCommittedGoldenBytes()
    {
        var tree = CreateStandardTree(); // FX-GOLDEN: same 100% deterministic tree

        var report = GenerateReportV1(tree.Root, tree.Entries.ToArray(), new Blake3Hasher());

        var text = Encoding.UTF8.GetString(report);
        var masked = MaskRoot(text, tree.Root);
        var comparableBytes = Encoding.UTF8.GetBytes(masked);

        var goldenSource = GoldenSourcePath();
        var goldenEmbedded = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "golden", "fx-golden-report-v1.json");

        if (Environment.GetEnvironmentVariable("DOCTOR_REGEN_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenSource)!);
            File.WriteAllText(goldenSource, masked, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return; // controlled regeneration: exit green after writing (tdd-deterministic-tooling skill)
        }

        if (!File.Exists(goldenEmbedded) && !File.Exists(goldenSource))
        {
            Assert.Fail(
                "DET-06 RED: golden missing (tests/Doctor.Tests/Fixtures/golden/fx-golden-report-v1.json). " +
                "Generate with: DOCTOR_REGEN_GOLDENS=1 dotnet test --filter FullyQualifiedName~DeterminismSuiteTests");
        }

        var goldenBytes = File.Exists(goldenEmbedded)
            ? File.ReadAllBytes(goldenEmbedded)
            : File.ReadAllBytes(goldenSource);

        AssertBytesIdentical(goldenBytes, comparableBytes, "DET-06 golden fx-golden-report-v1.json");
    }

    /// <summary>Golden source directory (bin → project traversal), for regeneration.</summary>
    private static string GoldenSourcePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Doctor.Tests.csproj")))
            {
                return Path.Combine(dir.FullName, "Fixtures", "golden", "fx-golden-report-v1.json");
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("test project root not found from bin.");
    }

    /// <summary>
    /// Replaces ONLY the generated_from.root_path value (absolute, varies per
    /// machine/OS) with the sentinel "&lt;ROOT&gt;". No other part of the report is touched.
    /// </summary>
    private static string MaskRoot(string text, string root)
    {
        var slashVariant = root.Replace('\\', '/');
        var backslashVariant = root.Replace('/', '\\');

        text = text.Replace(slashVariant, "<ROOT>", StringComparison.Ordinal);
        if (backslashVariant != slashVariant)
        {
            text = text.Replace(backslashVariant, "<ROOT>", StringComparison.Ordinal);
        }

        return text;
    }

    // =====================================================================================
    // Standard 50-file tree — 25 paired + 10 conflicts + 15 uniques
    // =====================================================================================

    private sealed record TestTree(string Root, IReadOnlyList<FileEntry> Entries);

    /// <summary>
    /// Card-required composition, 100% deterministic (names, sizes, contents and
    /// mtimes derived from indices):
    /// - 11 identical pairs (22 files) + 1 trio (3 files) = 25 paired;
    /// - 5 real conflict groups × 2 copies = 10 (same partial windows, different
    ///   cores ⇒ survive L2 and diverge at L3);
    /// - 15 uniques (five of them with trap names for DET-04).
    /// </summary>
    private TestTree CreateStandardTree()
    {
        var created = new List<(string Abs, int TimeIndex)>();
        var clock = 0;

        void Write(string relative, byte[] content)
        {
            var path = Path.Combine(new[] { _root }.Concat(relative.Split('/')).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            File.SetLastWriteTimeUtc(path, TimeBase(clock));
            created.Add((path, clock));
            clock++;
        }

        // ---- 11 identical pairs -----------------------------------------------------
        for (var i = 0; i < 11; i++)
        {
            var large = i % 2 == 0; // half large (windowed), half small (whole)
            var size = large ? LargeFile : (16 + i) * Kib;
            var content = StandardizedContent(seed: (byte)(0x30 + i), size);

            Write($"dups/p{i:00}/foto-p{i:00}.jpg", content);
            Write($"backup/p{i:00}/foto-p{i:00}.jpg", (byte[])content.Clone());
        }

        // ---- identical trio -----------------------------------------------------------
        var trioContent = StandardizedContent(seed: (byte)0xE0, LargeFile + 7);
        Write("trio/arq-trio.bin", trioContent);
        Write("trio/copy1/arq-trio.bin", (byte[])trioContent.Clone());
        Write("trio/copy2/arq-trio.bin", (byte[])trioContent.Clone());

        // ---- 5 real conflict groups (2 copies each) --------------------------------
        for (var k = 0; k < 5; k++)
        {
            var host = $"HOST0{k}"; // 6 alphanumeric characters (normalizer contract)
            // Plain: core (0x10+k, 0x50+k); mirror: inverted core (0x50+k, 0x10+k).
            // Same windows (head/tail) → guaranteed partial collision.
            // Different core → divergent full hash → real conflict.
            Write($"conflicts/orc-k{k:00}.bin", ConflictContent(corePlain: (byte)(0x10 + k), coreMirror: (byte)(0x50 + k)));
            Write($"conflicts/mirror/orc-k{k:00}-DESKTOP-{host}.bin", ConflictContent(corePlain: (byte)(0x50 + k), coreMirror: (byte)(0x10 + k)));
        }

        // ---- 15 uniques (first five = DET-04 trap names) -------------------
        string[] trapNames =
        [
            "Zebra.txt",
            "apple.txt",
            "Apple.txt",
            "zebra.txt",
            "Árvore.txt",
        ];

        for (var u = 0; u < 15; u++)
        {
            var name = u < trapNames.Length ? trapNames[u] : $"arq-u{u:00}.txt";
            var size = 5000 + (u * 997);
            Write($"unique/{name}", StandardizedContent(seed: (byte)(0x70 + u), size));
        }

        // Level 0 entries in CREATION order (= "real" physical order of the proof).
        var entries = created
            .Select(pair =>
            {
                var info = new FileInfo(pair.Abs);
                return new FileEntry
                {
                    Path = pair.Abs,
                    Size = info.Length,
                    MtimeUtc = new DateTimeOffset(TimeBase(pair.TimeIndex), TimeSpan.Zero),
                    Attributes = FileAttributes.Normal,
                    VolumeId = "det-suite",
                    FileId = pair.Abs,
                };
            })
            .ToList();

        Assert.Equal(50, entries.Count); // card composition: 25 + 10 + 15

        return new TestTree(_root, entries);
    }

    /// <summary>In-MEMORY universe of the same 50 entries (unit: DET-04/05 without disk).</summary>
    private static List<FileEntry> UniverseInMemory()
    {
        var list = new List<FileEntry>();
        var index = 0;

        void Add(string relative, long size)
        {
            list.Add(new FileEntry
            {
                Path = "/universe-det/" + relative,
                Size = size,
                MtimeUtc = TimeBaseOffset(index),
                Attributes = FileAttributes.Normal,
                VolumeId = "det-suite",
                FileId = relative,
            });
            index++;
        }

        for (var i = 0; i < 11; i++)
        {
            var size = i % 2 == 0 ? LargeFile : (16 + i) * Kib;
            Add($"dups/p{i:00}/foto-p{i:00}.jpg", size);
            Add($"backup/p{i:00}/foto-p{i:00}.jpg", size);
        }

        Add("trio/arq-trio.bin", LargeFile + 7);
        Add("trio/copy1/arq-trio.bin", LargeFile + 7);
        Add("trio/copy2/arq-trio.bin", LargeFile + 7);

        for (var k = 0; k < 5; k++)
        {
            Add($"conflicts/orc-k{k:00}.bin", LargeFile);
            Add($"conflicts/mirror/orc-k{k:00}-DESKTOP-HOST0{k}.bin", LargeFile);
        }

        string[] trapNames = ["Zebra.txt", "apple.txt", "Apple.txt", "zebra.txt", "Árvore.txt"];
        for (var u = 0; u < 15; u++)
        {
            var name = u < trapNames.Length ? trapNames[u] : $"arq-u{u:00}.txt";
            Add($"unique/{name}", 5000 + (u * 997));
        }

        Assert.Equal(50, list.Count);
        return list;
    }

    private static DateTime TimeBase(int index) => TimeBaseOffset(index).UtcDateTime;

    private static DateTimeOffset TimeBaseOffset(int index) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index);

    private static byte[] StandardizedContent(byte seed, int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + (i % 251));
        }

        return bytes;
    }

    /// <summary>Fixed head and tail (guaranteed partial collision); two different cores.</summary>
    private static byte[] ConflictContent(byte corePlain, byte coreMirror)
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

        var middle = LargeFile - Window;
        for (var i = Window; i < middle; i++)
        {
            // Core alternates by 4096-byte BLOCKS between the two values: copies
            // maintain identical partial windows [0,64K)+[end-64K,end) (same head/tail
            // seed) and guaranteed divergent full hashes.
            bytes[i] = ((i / 4096) % 2 == 0) ? corePlain : coreMirror;
        }

        return bytes;
    }

    // =====================================================================================
    // Pipeline execution + deterministic projections
    // =====================================================================================

    private sealed record TwoLayerOutput(byte[] CanonicalResult, byte[] ReportV1, ScanTelemetry Telemetry);

    private ScanPipeline RunPipeline(FileEntry[] physicalOrder, IHasher hasher) =>
        new(new FakeFileEnumerator(physicalOrder), hasher);

    private TwoLayerOutput RunTwoLayerScan(string root, FileEntry[] physicalOrder, IHasher hasher)
    {
        var result = RunPipeline(physicalOrder, hasher).Run(root);

        var canonical = JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        var report = GenerateReportV1(root, physicalOrder, hasher);

        return new TwoLayerOutput(canonical, report, BuildTelemetry(result, physicalOrder.Length));
    }

    /// <summary>
    /// Full schema v1 report with FROZEN timestamps (condition §2.3) — the only
    /// time allowed comes from fixed arguments, never from the clock.
    /// </summary>
    private byte[] GenerateReportV1(string root, FileEntry[] physicalOrder, IHasher hasher)
    {
        var result = RunPipeline(physicalOrder, hasher).Run(root);
        var telemetry = BuildTelemetry(result, physicalOrder.Length);

        using var output = new MemoryStream();
        new ReportWriterJson().Write(
            result,
            telemetry,
            PlaceholderReport.Records(result.Groups.SelectMany(g => g.Members)),
            root,
            FrozenStart,
            FrozenEnd,
            output);

        return output.ToArray();
    }

    /// <summary>
    /// Telemetry derived from recorded decisions (same contract as T16/ScanCommand):
    /// partial for every candidate group member, full for groups with a verdict,
    /// ADR-0005 byte recipe via hasher constants.
    /// </summary>
    private static ScanTelemetry BuildTelemetry(ScanResult result, int enumerated)
    {
        var l2Members = result.Groups
            .Where(g => g.Members.Count >= 2)
            .SelectMany(g => g.Members)
            .ToArray();

        var l3Sizes = result.IdenticalDuplicates
            .SelectMany(d => d.Files.Select(f => f.Size))
            .Concat(result.RealConflicts.SelectMany(c => c.Files.Select(_ => c.SizeBytes)))
            .ToArray();

        return new ScanTelemetry
        {
            FilesEnumerated = enumerated,
            FilesSkipped = 0,
            FilesPlaceholder = 0,
            FilesPartialHashed = l2Members.Length,
            FilesFullHashed = l3Sizes.Length,
            BytesReadPartial = l2Members.Sum(PartialBytes),
            BytesReadFull = l3Sizes.Sum(),
            PlaceholderBytesRead = 0,
        };
    }

    /// <summary>ADR-0005 v1 recipe: ≤ 128 KiB reads whole file; above, two 64 KiB windows.</summary>
    private static long PartialBytes(FileEntry file) =>
        file.Size <= Blake3Hasher.WholeFileLimitBytes
            ? file.Size
            : 2L * Blake3Hasher.WindowBytes;

    // =====================================================================================
    // Byte-by-byte comparison with readable diff
    // =====================================================================================

    /// <summary>
    /// Compares two byte arrays; on divergence, FAILS with diff: sizes,
    /// first divergent offset, hex excerpt around it and up to 12 divergent
    /// text lines from each side (JSON is readable UTF-8).
    /// </summary>
    private static void AssertBytesIdentical(byte[] expected, byte[] obtained, string context)
    {
        if (expected.AsSpan().SequenceEqual(obtained))
        {
            return;
        }

        var message = new StringBuilder();
        message.AppendLine($"[{context}] outputs DIFFER byte by byte.");
        message.AppendLine($"expected bytes: {expected.Length}; obtained bytes: {obtained.Length}.");

        var common = Math.Min(expected.Length, obtained.Length);
        var first = 0;
        while (first < common && expected[first] == obtained[first])
        {
            first++;
        }

        message.AppendLine($"first divergent byte at offset {first}.");

        const int ctx = 32;
        var start = Math.Max(0, first - ctx);
        var endExpected = Math.Min(expected.Length, first + ctx);
        var endObtained = Math.Min(obtained.Length, first + ctx);

        message.AppendLine($"expected [{start}..{endExpected}): {Hex(expected, start, endExpected)}");
        message.AppendLine($"obtained [{start}..{endObtained}): {Hex(obtained, start, endObtained)}");

        foreach (var line in DiffLines(expected, obtained))
        {
            message.AppendLine(line);
        }

        Assert.Fail(message.ToString());
    }

    private static string Hex(byte[] bytes, int start, int end)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
            if ((i - start + 1) % 16 == 0)
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> DiffLines(byte[] expected, byte[] obtained)
    {
        var expectedLines = Encoding.UTF8
            .GetString(expected)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        var obtainedLines = Encoding.UTF8
            .GetString(obtained)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        const int maxLines = 12;
        var shown = 0;
        var total = Math.Max(expectedLines.Length, obtainedLines.Length);

        for (var i = 0; i < total && shown < maxLines; i++)
        {
            var lineE = i < expectedLines.Length ? expectedLines[i] : "<missing>";
            var lineO = i < obtainedLines.Length ? obtainedLines[i] : "<missing>";

            if (!string.Equals(lineE, lineO, StringComparison.Ordinal))
            {
                yield return $"diff @{i}: expected | {lineE}";
                yield return $"diff @{i}: obtained | {lineO}";
                shown += 2;
            }
        }

        if (shown >= maxLines)
        {
            yield return "diff truncated at 12 divergent lines.";
        }
    }

    // =====================================================================================
    // Utilities
    // =====================================================================================

    /// <summary>Fisher-Yates with fixed seed — deterministic, no real randomness.</summary>
    private static FileEntry[] Shuffle(IEnumerable<FileEntry> original, int seed)
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

    /// <summary>INDEPENDENT oracle from the product: UTF-8 byte lexicographic order.</summary>
    private static int CompareByUtf8Bytes(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        var common = Math.Min(bytesA.Length, bytesB.Length);

        for (var i = 0; i < common; i++)
        {
            if (bytesA[i] != bytesB[i])
            {
                return bytesA[i].CompareTo(bytesB[i]);
            }
        }

        return bytesA.Length.CompareTo(bytesB.Length);
    }

    /// <summary>
    /// Fake enumerator (L0): returns entries EXACTLY in the requested physical order —
    /// forward, reverse, or shuffled. Canonical ordering is the pipeline's
    /// responsibility, and that is what DET-01/02/03 verify (T12 pattern).
    /// </summary>
    private sealed class FakeFileEnumerator : IFileEnumerator
    {
        private readonly IReadOnlyList<FileEntry> _order;

        public FakeFileEnumerator(IReadOnlyList<FileEntry> order) => _order = order;

        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) =>
            new(_order, Array.Empty<ScanError>(), new ScanTelemetry());
    }
}
