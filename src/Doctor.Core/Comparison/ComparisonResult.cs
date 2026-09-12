namespace Doctor.Core;

/// <summary>
/// Document comparison contract (docs/contracts.md — single source; SPEC §16;
/// ADR-0011). Selection by case-insensitive extension; unknown type ⇒
/// <see cref="BinaryFallbackComparator"/>. Both files must be readable and
/// non-placeholder (inherited gate from hasher, ADR-0011 item 4) — placeholder
/// throws <see cref="PlaceholderReadException"/> BEFORE any opening.
/// Structured result, ordered, no incidental time field.
/// </summary>
public interface IDocumentComparator
{
    /// <summary>Compares the content of two files and returns regions sorted by position.</summary>
    ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct);
}

/// <summary>
/// Structured comparison result (contracts.md — exact). <see cref="Regions"/>
/// in ascending document position order; no clock information.
/// </summary>
public sealed record ComparisonResult(
    string ComparatorKind,
    bool AreSemanticallyEqual,
    IReadOnlyList<DiffRegion> Regions);
