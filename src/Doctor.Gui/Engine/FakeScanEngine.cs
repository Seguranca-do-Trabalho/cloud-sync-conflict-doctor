using System.Text;

namespace Doctor.Gui.Engine;

/// <summary>
/// Fake engine: fixed DEMO demonstration data — no filesystem access.
/// Structured in the shape of schema v1 (docs/schema-report-v1.md): telemetry with the
/// 9 counters and invariants maintained, lists pre-sorted by path in UTF-8
/// bytes (§3). Selectable scenarios cover the card's edge cases: nominal,
/// zero duplicates, zero conflicts, placeholders only.
/// </summary>
public sealed class FakeScanEngine : IScanEngine
{
    /// <summary>Selectable DEMO scenarios to exercise GUI edge cases.</summary>
    public enum ScanScenario
    {
        /// <summary>Identical duplicates + real conflict + placeholders.</summary>
        Nominal,
        /// <summary>Edge: no identical duplicate classes; conflicts and placeholders present.</summary>
        NoDuplicates,
        /// <summary>Edge: no group with real divergence; duplicates and placeholders present.</summary>
        NoConflicts,
        /// <summary>Edge: placeholders only; no content bytes are read.</summary>
        PlaceholdersOnly,
    }

    private readonly ScanScenario _scenario;

    public FakeScanEngine(ScanScenario scenario = ScanScenario.Nominal) => _scenario = scenario;

    // Partial hash window (Level 2): head + tail of 64 KiB.
    private const long PartialWindowBytes = 64 * 1024;

    public ScanReport Scan(string rootPath, Action<int>? progress = null)
    {
        progress?.Invoke(0);
        var placeholdersOnly = _scenario == ScanScenario.PlaceholdersOnly;

        var duplicates = _scenario == ScanScenario.NoDuplicates || placeholdersOnly
            ? []
            : new[]
            {
                new DuplicateGroup
                {
                    Blake3Hash = "demo-a1b2c3d4e5f60718293a4b5c6d7e8f90",
                    SizeBytes = 1_048_576,
                    Files = Sorted("Relatorios/2026/copia-relatorio-anual.docx",
                                   "Relatorios/2026/relatorio-anual (1).docx",
                                   "Relatorios/2026/relatorio-anual.docx"),
                },
                new DuplicateGroup
                {
                    Blake3Hash = "demo-b2c3d4e5f60718293a4b5c6d7e8f90a1",
                    SizeBytes = 2_458_912,
                    Files = Sorted("Fotos/viagem-2025/praia - copia.jpg",
                                   "Fotos/viagem-2025/praia.jpg"),
                },
            };

        var conflictsRaw = new[]
        {
            new ConflictGroup
            {
                BaseName = "Projetos/orcamento.xlsx",
                TotalBytes = 91_077,
                Versions =
                [
                    new ConflictVersion
                    {
                        Path = "Projetos/orcamento.xlsx",
                        SizeBytes = 88_412,
                        MtimeUtc = new DateTimeOffset(2026, 8, 10, 14, 32, 0, TimeSpan.Zero),
                        Blake3Hash = "demo-c3d4e5f60718293a4b5c6d7e8f90a1b2",
                    },
                    new ConflictVersion
                    {
                        Path = "Projetos/orcamento (Notebook-Office).xlsx",
                        SizeBytes = 91_077,
                        MtimeUtc = new DateTimeOffset(2026, 8, 12, 9, 15, 0, TimeSpan.Zero),
                        Blake3Hash = "demo-d4e5f60718293a4b5c6d7e8f90a1b2c3",
                    },
                    new ConflictVersion
                    {
                        Path = "Projetos/orcamento (DESKTOP-4K2F conflicted copy 2026-08-13).xlsx",
                        SizeBytes = 90_240,
                        MtimeUtc = new DateTimeOffset(2026, 8, 13, 18, 47, 0, TimeSpan.Zero),
                        Blake3Hash = "demo-e5f60718293a4b5c6d7e8f90a1b2c3d4",
                    },
                ],
            },
        };

        var conflicts = _scenario == ScanScenario.NoConflicts || placeholdersOnly
            ? []
            : conflictsRaw;


        // Placeholders: NEVER opened. Canonical labels from schema v1 §6.3.
        var placeholdersBase = new[]
        {
            new PlaceholderFile
            {
                Path = "Arquivados/backup-antigo.pst",
                Kinds = ["offline"],
                SizeBytes = 1_873_285_120,
                Reason = "FILE_ATTRIBUTE_OFFLINE",
            },
            new PlaceholderFile
            {
                Path = "Videos/apresentacao-final.mp4",
                Kinds = ["recall_on_open"],
                SizeBytes = 512_450_112,
                Reason = "RECALL_ON_OPEN",
            },
        };
        var placeholders = placeholdersBase;

        var report = new ScanReport
        {
            RootPath = rootPath,
            Telemetry = CalculateTelemetry(duplicates, conflicts, placeholders, placeholdersOnly),
            IdenticalDuplicates = duplicates,
            RealConflicts = conflicts,
            Placeholders = placeholders,
        };

        // Deterministic fake progress: simulates pipeline phases without real I/O.
        foreach (var step in new[] { 20, 45, 70, 90, 100 })
        {
            progress?.Invoke(step);
        }

        return report;
    }

    /// <summary>
    /// Telemetry derived from the DEMO data itself — counters count what the
    /// scenario represents, not loose numbers:
    /// enumerated = placeholders + singletons (skipped) + partially hashed;
    /// bytes read follow the Level 2 windows (≤ 128 KiB: whole file once)
    /// and the whole file of Level 3 survivors.
    /// </summary>
    private static ScanTelemetry CalculateTelemetry(
        IReadOnlyList<DuplicateGroup> duplicates,
        IReadOnlyList<ConflictGroup> conflicts,
        IReadOnlyList<PlaceholderFile> placeholders,
        bool placeholdersOnly)
    {
        if (placeholdersOnly)
        {
            // Placeholders-only scenario: nothing else exists in the DEMO tree.
            return new ScanTelemetry
            {
                FilesEnumerated = placeholders.Count,
                FilesPlaceholder = placeholders.Count,
                FilesSkipped = 0,
                FilesPartialHashed = 0,
                FilesFullHashed = 0,
                BytesRead = 0,
                BytesReadPartial = 0,
                BytesReadFull = 0,
                PlaceholderBytesRead = 0,
            };
        }

        // Singletons outside groups: never hashed → files_skipped (schema §5).
        const long singletonsSkipped = 4;

        var contentVersions = duplicates.Sum(d => d.Files.Count)
                              + conflicts.SelectMany(c => c.Versions).Count();

        var partialHashed = contentVersions + 2; // +2 eliminated still at Level 2

        // Partial-pass bytes: large files pay 2×64 KiB; small ones (≤128 KiB),
        // the whole file a single time.
        long bytesReadPartial = duplicates.Sum(g => PartialOf(g.SizeBytes, g.Files.Count))
                                + conflicts.SelectMany(c => c.Versions)
                                           .Sum(v => PartialOf(v.SizeBytes, 1));

        // Full-pass bytes: only the Level 2 survivors — which are exactly the
        // versions present in the DEMO collections (the 2 eliminated at L2
        // don't appear in any group, only in the files_partial_hashed counter).
        long bytesReadFull =
            duplicates.Sum(d => d.SizeBytes * d.Files.Count)
            + conflicts.SelectMany(c => c.Versions).Sum(v => v.SizeBytes);

        return new ScanTelemetry
        {
            FilesEnumerated = placeholders.Count + singletonsSkipped + partialHashed,
            FilesSkipped = singletonsSkipped,
            FilesPlaceholder = placeholders.Count,
            FilesPartialHashed = partialHashed,
            FilesFullHashed = contentVersions,
            BytesRead = bytesReadPartial + bytesReadFull,
            BytesReadPartial = bytesReadPartial,
            BytesReadFull = bytesReadFull,
            PlaceholderBytesRead = 0,
        };
    }

    /// <summary>L2 partial-pass bytes per file: min(size, 64 KiB) × 2 (head + tail).</summary>
    private static long PartialOf(long sizeBytes, int copies) =>
        copies * (Math.Min(sizeBytes, PartialWindowBytes)
                  + Math.Min(sizeBytes - PartialWindowBytes > 0
                      ? sizeBytes - PartialWindowBytes
                      : 0,
                      PartialWindowBytes));

    /// <summary>Sort by UTF-8 bytes of the path (§3 / schema §1.3), never locale.</summary>
    private static IReadOnlyList<string> Sorted(params string[] files) =>
        files.OrderBy(f => Encoding.UTF8.GetBytes(f), Comparer<byte[]>.Create(
            (a, b) => a.AsSpan().SequenceCompareTo(b.AsSpan()))).ToList();
}
