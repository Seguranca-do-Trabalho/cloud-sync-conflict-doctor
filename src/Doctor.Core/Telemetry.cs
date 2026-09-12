using System.Text;

namespace Doctor.Core;

/// <summary>
/// Internal scanner telemetry contract (SPEC §10) — the product's main metric
/// is the number of bytes that DID NOT need to be read.
///
/// Fields in the EXACT order fixed by the orchestrator (card T06): the eight SPEC §10
/// counters in snake_case, followed by the placeholder_bytes_read gate required by
/// docs/benchmark-harness.md §6 (run is rejected if != 0). Deterministic JSON emission
/// follows this order; changing it is a contract change and requires registration in a
/// new ADR.
///
/// Normative semantics:
///   files_enumerated       = files_placeholder + files_skipped + files_partial_hashed
///   files_full_hashed     &lt;= files_partial_hashed
///   bytes_read             = bytes_read_partial + bytes_read_full
///   placeholder_bytes_read = 0 always (value != 0 is a security violation, not data)
/// </summary>
public sealed record ScanTelemetry
{
    /// <summary>Total file entries seen at Level 0 (directories don't count). Includes placeholders.</summary>
    public long FilesEnumerated { get; init; }

    /// <summary>Non-placeholder entries that had ZERO bytes read.</summary>
    public long FilesSkipped { get; init; }

    /// <summary>Entries classified as placeholder and excluded from all content access.</summary>
    public long FilesPlaceholder { get; init; }

    /// <summary>
    /// Entries excluded from enumeration for being under the reserved subtree
    /// &lt;root&gt;/ConflictDoctor/ (quarantine §18/SPEC; T-15 addendum, SEG-12): structural
    /// policy of the Level 0 canonical boundary, not an error — never enters Errors nor
    /// any file counter (R10; idempotency §20 across rescans).
    /// </summary>
    public long FilesExcludedConflictDoctor { get; init; }

    /// <summary>Non-placeholder entries that received partial hash (start window + end window).</summary>
    public long FilesPartialHashed { get; init; }

    /// <summary>Entries that survived partial hash and received full BLAKE3. Subset of FilesPartialHashed.</summary>
    public long FilesFullHashed { get; init; }

    /// <summary>Sum of bytes actually read: BytesReadPartial + BytesReadFull.</summary>
    public long BytesRead => checked(BytesReadPartial + BytesReadFull);

    /// <summary>Bytes read in the partial hash pass (files ≤ 128 KiB are read in full in this count).</summary>
    public long BytesReadPartial { get; init; }

    /// <summary>Bytes read in the full hash pass.</summary>
    public long BytesReadFull { get; init; }

    /// <summary>Absolute gate (benchmark-harness.md §6): bytes read from placeholders. ALWAYS 0; value != 0 rejects the run.</summary>
    public long PlaceholderBytesRead { get; init; }

    /// <summary>
    /// Aggregates counters from another snapshot into this one (e.g.: shards of the same run).
    /// Sums all counters; bytes_read remains derived from the parts and the
    /// placeholder_bytes_read gate only remains zero if zero in BOTH operands.
    /// </summary>
    public ScanTelemetry Merge(ScanTelemetry other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new ScanTelemetry
        {
            FilesEnumerated = checked(FilesEnumerated + other.FilesEnumerated),
            FilesSkipped = checked(FilesSkipped + other.FilesSkipped),
            FilesPlaceholder = checked(FilesPlaceholder + other.FilesPlaceholder),
            FilesPartialHashed = checked(FilesPartialHashed + other.FilesPartialHashed),
            FilesFullHashed = checked(FilesFullHashed + other.FilesFullHashed),
            BytesReadPartial = checked(BytesReadPartial + other.BytesReadPartial),
            BytesReadFull = checked(BytesReadFull + other.BytesReadFull),
            PlaceholderBytesRead = checked(PlaceholderBytesRead + other.PlaceholderBytesRead),
        };
    }
}
