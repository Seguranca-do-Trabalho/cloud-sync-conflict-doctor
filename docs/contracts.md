# Module Contracts — Cloud Sync Conflict Doctor

Contracts for the key interfaces in `src/Doctor.Core`. All implementations must respect the annotated determinism semantics. Root namespace: `Doctor.Core`. Cross-cutting rule: **canonical order** = path in UTF-8 bytes (`StringComparer.Ordinal` on the normalized path form); tie-break in any decision = `mtime` → `size` → `path` (ADR-0003).

```csharp
namespace Doctor.Core;

/// <summary>Canonical order across the entire product: UTF-8 bytes of path; tie-break mtime → size → path.</summary>
public static class PathOrder
{
    public static int Compare(FileEntry a, FileEntry b);
    public static IOrderedEnumerable<T> Sort<T>(IEnumerable<T> items, Func<T, FileEntry> key);
}

/// <summary>Metadata collected at Level 0. Immutable after creation.</summary>
public sealed record FileEntry
{
    public required string Path { get; init; }              // full normalized path
    public required long Size { get; init; }
    public required DateTimeOffset MtimeUtc { get; init; }
    public required FileAttributes Attributes { get; init; }
    public required string VolumeId { get; init; }
    public required string FileId { get; init; }            // NTFS file ID / equivalent
    public bool IsPlaceholder { get; init; }                // OFFLINE / RECALL_* / reparse point
    public PlaceholderKind? PlaceholderKind { get; init; }
}
```

## IFileEnumerator

```csharp
public interface IFileEnumerator
{
    /// <summary>Enumerates metadata (Level 0) without reading content. Never traverses directory
    /// reparse points. Output is ALWAYS sorted by PathOrder before returning —
    /// regardless of the physical filesystem order. Does not throw per individual file:
    /// access errors go to ScanErrors and do not abort the scan.</summary>
    EnumerationResult Enumerate(string rootPath, CancellationToken ct);
}

public sealed record EnumerationResult(
    IReadOnlyList<FileEntry> Files,      // sorted by PathOrder — contract
    IReadOnlyList<ScanError> Errors,
    ScanTelemetry Telemetry);
```

Determinism: the implementer may enumerate in any internal order but **must** deliver the collection sorted by `PathOrder`; the pipeline never reorders on its own afterwards.

## IHasher

```csharp
public interface IHasher
{
    /// <summary>Partial BLAKE3 hash (ADR-0005): file ≤ 128 KiB reads entirely;
    /// > 128 KiB reads windows [0,64KiB) + [size-64KiB,size) in a single open.
    /// Throws PlaceholderReadException if entry.IsPlaceholder — invariants: no placeholder
    /// byte is read (placeholder_bytes_read == 0).</summary>
    string PartialHash(FileEntry entry, CancellationToken ct);

    /// <summary>Full BLAKE3 hash. Same placeholder gate.</summary>
    string FullHash(FileEntry entry, CancellationToken ct);
}
```

Determinism: hash depends only on content; parallel reading never changes the result. TOCTOU revalidation post-read (size+mtime) is the caller's responsibility via `ICacheStore`.

## ICacheStore

```csharp
public interface ICacheStore : IDisposable
{
    /// <summary>Hit only on exact match of (volume_id, file_id) + size + mtime
    /// + algorithm + hash_version (ADR-0006). Returns null on any divergence.</summary>
    CacheHit? Lookup(VolumeFileKey key, string algorithm, int hashVersion);

    /// <summary>Batch writes within a single transaction; crash leaves previous state intact.</summary>
    void Store(IReadOnlyList<CacheEntry> entries);

    /// <summary>Idempotent pruning of entries with last_seen before cutoff.</summary>
    int Prune(DateTimeOffset cutoffUtc);
}
```

Determinism: cache is a transparent optimization — presence or absence of a hit NEVER changes the report emitted, only bytes read.

## IScanPipeline

```csharp
public interface IScanPipeline
{
    /// <summary>Executes L0→L1→L2→L3 (ADR-0004). Serial decision over sorted collections;
    /// only read/hash parallel (parallelism via IConcurrencyPolicy).
    /// Single versioned output per ADR-0003.</summary>
    ScanReport Run(ScanRequest request, CancellationToken ct);
}

public sealed record ScanRequest(string RootPath, int ReadParallelism, bool UseCache);

public sealed record ScanReport
{
    public required int ReportSchemaVersion { get; init; }   // 1
    public required string Algorithm { get; init; }          // "BLAKE3"
    public required int HashVersion { get; init; }           // 1
    public required ScanTelemetry Telemetry { get; init; }   // includes placeholder_bytes_read
    public required IReadOnlyList<GroupReport> Groups;       // sorted PathOrder
    public required IReadOnlyList<IdenticalDuplicate> IdenticalDuplicates;
    public required IReadOnlyList<RealConflict> RealConflicts;
    public required IReadOnlyList<PlaceholderRecord> Placeholders;
}
```

Determinism: `Run` is a pure function of the tree + declared configuration; same input ⇒ byte-for-byte identical report (§3, §20), even under distinct enumeration orders.

## IReportWriter

```csharp
public interface IReportWriter
{
    /// <summary>Serializes ScanReport exactly to v1 schema (ADR-0003). Deterministic
    /// serialization: properties in fixed order, lists pre-sorted by domain,
    /// no incidental timestamps inside lists. Atomic write
    /// (tmp + rename) when writing to file.</summary>
    void Write(ScanReport report, TextWriter output);
    void WriteToFile(ScanReport report, string path);
}
```

Determinism: two logically identical reports produce byte-identical files; numeric formatting culture-invariant (`InvariantCulture`).

## IQuarantine

```csharp
public interface IQuarantine
{
    /// <summary>Moves items to <root>/ConflictDoctor/quarantine/<op_id>/payload with atomic
    /// manifest (ADR-0010). NEVER deletes. Failure => honest partial status; nothing silent.</summary>
    QuarantineResult Quarantine(IReadOnlyList<QuarantineItem> items, string rootPath, CancellationToken ct);

    /// <summary>Validates payload hash and restores. Occupied destination => restores with
    /// .restored-<op_id> suffix; NEVER overwrites (ADR-0010 §4).</summary>
    RestoreResult Restore(string operationId, string rootPath, CancellationToken ct);
}
```

Determinism: `<op_id>` = UTC timestamp + 8 hex derived from manifest content — reproducible, not random (ADR-0010 §1).

## IDocumentComparator

```csharp
public interface IDocumentComparator
{
    /// <summary>Selection by case-insensitive extension; unknown => BinaryFallbackComparator
    /// (ADR-0011). Both files must be readable and non-placeholder (gate inherited from hasher).
    /// Structured, sorted result with no incidental time fields.</summary>
    ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct);
}

public sealed record ComparisonResult(
    string ComparatorKind,                 // "text" | "markdown" | "csv" | "binary"
    bool AreSemanticallyEqual,
    IReadOnlyList<DiffRegion> Regions);    // sorted by position in document
```

Determinism: same file pair ⇒ same `ComparisonResult`, always; GUI only renders (§13).

## IMediaProbe

```csharp
public interface IMediaProbe
{
    /// <summary>Detects seek penalty of the volume hosting rootPath (ADR-0007).
    /// Windows-native implementation via IOCTL_STORAGE_QUERY_PROPERTY; fakes in tests.
    /// Unavailable/unknown => conservative profile (low parallelism).</summary>
    ReadProfile ProbeVolume(string rootPath);
}

public sealed record ReadProfile(bool HasSeekPenalty, int RecommendedParallelism);
```

Determinism: affects only performance; never changes report content.

## IConcurrencyPolicy

```csharp
public interface IConcurrencyPolicy
{
    /// <summary>Resolves effective parallelism: flag > config > media > conservative default
    /// min(ProcessorCount, 4) (ADR-0007 §4). Value >= 1 always.</summary>
    int ResolveReadParallelism(int? explicitFlag);
}
```

---

## Key Risks and Mitigations

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | Accidental placeholder read (induced download of online-only tree) | High: massive bandwidth cost, violates §6 | Gate in `IHasher` + `IsPlaceholder` at L0; automated test `placeholder_bytes_read == 0` in CI (§21); enumeration does not traverse reparse points |
| R2 | Non-determinism between runs (thread order, locale, hash map) | High: breaks the product's core promise | Serial decision over collections sorted by `PathOrder`; culture-invariant serialization; CI test of 3 scans with different orders byte-identical (§20) |
| R3 | Data loss/corruption during resolution | Critical: product trust dies | No delete in production (ADR-0002); quarantine+atomic manifest; restore validates hash and never overwrites; GATE 3 blocks downstream; anti-delete static review |
| R4 | TOCTOU — file changes between L0 and read | Medium: hash assigned to wrong content | Post-read size+mtime revalidation (ADR-0005 §7); persisting divergence ⇒ partial error (exit 3), never silence |
| R5 | Cache serving stale hash | Medium: false "identical" / lost conflict | Composite key volume_id+file_id+size+mtime+algorithm+hash_version (ADR-0006); cache never promotes partial to full; invalidation tests |
| R6 | Overly aggressive conflict normalization grouping unrelated files | Medium: false conflict positives | Small fixed versioned list (`normalizer_version`); groups are candidates — final decision always by real hash (L2/L3) |
| R7 | Poorly calibrated parallelism on HDD | Low: perceived slowness | `IMediaProbe` abstracts seek penalty; conservative default; user can pin `--read-parallelism`; tuning only with benchmark (§24) |
| R8 | Office semantic diff delaying v1 | Medium: deadline | Question A isolated on its own card; interface already accommodates future implementation without refactor (ADR-0011) |
| R9 | Compromised NuGet package (supply chain) | Medium: security | Pinned versions in `Directory.Packages.props`; minimal surface (Blake3, Microsoft.Data.Sqlite, System.CommandLine, Avalonia); dependency review at GATE 5 |
| R10 | Long paths/Unicode breaking Windows enumeration | Medium: silently partial tree scan | Per-file errors become `ScanError` listed in report (never silent abort); extended prefix `\\?\` when applicable |

Risks R1–R3 are blocking for their respective gates (GATE 2 and GATE 3); the rest are monitored by benchmark/review.
