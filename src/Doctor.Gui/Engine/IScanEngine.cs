using System.Text;

namespace Doctor.Gui.Engine;

/// <summary>
/// Single contract between the GUI and any scan engine (fake or real).
/// The GUI knows no implementation: it consumes only this interface.
/// The real engine (Doctor.Core) replaces FakeScanEngine without touching the screens.
/// </summary>
public interface IScanEngine
{
    /// <summary>Runs the scan of the given folder, reporting progress 0..100.</summary>
    ScanReport Scan(string rootPath, Action<int>? progress = null);
}

/// <summary>
/// Minimal domain model consumed by the GUI — in the shape of the report
/// schema v1 (docs/schema-report-v1.md), which takes precedence over the
/// ADR-0003 example. Local DEMO models for the GUI until GATE 2 closes;
/// field names follow the schema.
/// </summary>
public sealed class ScanReport
{
    public int ReportSchemaVersion { get; init; } = 1;
    public string Algorithm { get; init; } = "BLAKE3";
    public int HashVersion { get; init; } = 1;
    public string RootPath { get; init; } = "";
    public ScanTelemetry Telemetry { get; init; } = new();
    public IReadOnlyList<DuplicateGroup> IdenticalDuplicates { get; init; } = [];
    public IReadOnlyList<ConflictGroup> RealConflicts { get; init; } = [];
    public IReadOnlyList<PlaceholderFile> Placeholders { get; init; } = [];

    /// <summary>Identical duplicates = removable redundant copies (does not count the kept copy).</summary>
    public int IdenticalDuplicateCount => IdenticalDuplicates.Sum(g => g.Files.Count - 1);

    /// <summary>
    /// Recoverable space: sum of sizes of items eligible for quarantine —
    /// the losers from identical duplicates (all except the kept one, shortest path
    /// in UTF-8 bytes) plus the versions that will NOT be kept in real conflicts
    /// (deterministic suggestion mtime → size → path). The formula is explicitly tested.
    /// </summary>
    public long RecoverableBytes =>
        IdenticalDuplicates.Sum(g =>
            (long)(g.Files.Count - 1)
            * g.SizeBytes)
        + RealConflicts.Sum(g => g.SumVersionsBytes() - SuggestVersionToKeep(g).SizeBytes);

    /// <summary>Deterministic suggestion (SPEC §17): mtime → size → path in UTF-8 bytes.</summary>
    internal static ConflictVersion SuggestVersionToKeep(ConflictGroup group) =>
        group.Versions
            .OrderByDescending(v => v.MtimeUtc.UtcTicks)
            .ThenByDescending(v => v.SizeBytes)
            .ThenBy(v => Encoding.UTF8.GetBytes(v.Path),
                Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b.AsSpan())))
            .First();

    /// <summary>
    /// Validates the normative telemetry invariants of schema v1 §5.
    /// Empty = compliant; any item = violation named by the counter.
    /// </summary>
    internal IEnumerable<string> ValidateTelemetryInvariants()
    {
        var t = Telemetry;

        if (t.FilesEnumerated != t.FilesPlaceholder + t.FilesSkipped + t.FilesPartialHashed)
        {
            yield return $"files_enumerated ({t.FilesEnumerated}) ≠ files_placeholder " +
                         $"({t.FilesPlaceholder}) + files_skipped ({t.FilesSkipped}) + " +
                         $"files_partial_hashed ({t.FilesPartialHashed})";
        }

        if (t.FilesFullHashed > t.FilesPartialHashed)
        {
            yield return $"files_full_hashed ({t.FilesFullHashed}) > files_partial_hashed " +
                         $"({t.FilesPartialHashed})";
        }

        if (t.BytesRead != t.BytesReadPartial + t.BytesReadFull)
        {
            yield return $"bytes_read ({t.BytesRead}) ≠ bytes_read_partial " +
                         $"({t.BytesReadPartial}) + bytes_read_full ({t.BytesReadFull})";
        }

        if (t.PlaceholderBytesRead != 0)
        {
            yield return $"placeholder_bytes_read ({t.PlaceholderBytesRead}) ≠ 0 — " +
                         "security violation: placeholder is never opened";
        }
    }
}

/// <summary>
/// Telemetry with the 9 EXACT counters from schema v1 §5, in declared order:
/// files_enumerated, files_skipped, files_placeholder, files_partial_hashed,
/// files_full_hashed, bytes_read, bytes_read_partial, bytes_read_full,
/// placeholder_bytes_read. Integer counters ≥ 0, 64-bit width.
/// </summary>
public sealed class ScanTelemetry
{
    /// <summary>Total entries seen at Level 0 (directories never count). Includes placeholders.</summary>
    public long FilesEnumerated { get; init; }

    /// <summary>Non-placeholder entries that had zero bytes read (singleton groups).</summary>
    public long FilesSkipped { get; init; }

    /// <summary>Entries classified as placeholder and excluded from all content access.</summary>
    public long FilesPlaceholder { get; init; }

    /// <summary>Received partial hash at Level 2 (initial/final 64 KiB windows).</summary>
    public long FilesPartialHashed { get; init; }

    /// <summary>Survived Level 2 and received full BLAKE3 hash at Level 3.</summary>
    public long FilesFullHashed { get; init; }

    /// <summary>bytes_read_partial + bytes_read_full.</summary>
    public long BytesRead { get; init; }

    /// <summary>Bytes read in the partial pass; files ≤ 128 KiB count the whole file here.</summary>
    public long BytesReadPartial { get; init; }

    /// <summary>Bytes read during the full hash (Level 3).</summary>
    public long BytesReadFull { get; init; }

    /// <summary>Absolute rule §6/§7: always 0. Value ≠ 0 is a security failure, not data.</summary>
    public long PlaceholderBytesRead { get; init; }
}

public sealed class DuplicateGroup
{
    /// <summary>Full BLAKE3, 64 lowercase hex chars (schema v1 §6.1); DEMO uses demo- prefix.</summary>
    public string Blake3Hash { get; init; } = "";
    public long SizeBytes { get; init; }
    /// <summary>Sorted by path in UTF-8 bytes (determinism rule §3 / schema §1.3).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

public sealed class ConflictGroup
{
    /// <summary>Normalized base path of the group (schema v1 §6.2).</summary>
    public string BaseName { get; init; } = "";

    /// <summary>Common group size (schema v1 §6.2); divergent versions may differ from it in DEMO.</summary>
    public long TotalBytes { get; init; }

    public IReadOnlyList<ConflictVersion> Versions { get; init; } = [];

    /// <summary>Sum of bytes of all versions — direct input for the recoverable space formula.</summary>
    public long SumVersionsBytes() => Versions.Sum(v => v.SizeBytes);
}

public sealed class ConflictVersion
{
    public string Path { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTimeOffset MtimeUtc { get; init; }
    public string Blake3Hash { get; init; } = "";
    public bool IsPlaceholder { get; init; }
}

public sealed class PlaceholderFile
{
    public string Path { get; init; } = "";

    /// <summary>Canonical schema v1 §6.3 labels, e.g.: recall_on_open, offline.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>Size obtained from enumeration metadata (Level 0), without opening the file.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Human-readable reason derived from the labels (compatible with the T06 skeleton).</summary>
    public string Reason { get; init; } = "";
}
