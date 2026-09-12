namespace Doctor.Core;

/// <summary>
/// Product canonical order (SPEC §3, ADR-0003, docs/contracts.md): path compared
/// byte by byte in UTF-8 — StringComparer.Ordinal — and NEVER OrdinalIgnoreCase.
///
/// Documented choice: StringComparer.Ordinal compares Unicode code point to code
/// point; since .NET strings are UTF-16 and all BMP-relevant path characters encode
/// in UTF-8 in the same relative order of code points (code point order preserves
/// UTF-8 byte order for valid scalars), Ordinal is a stable, locale-independent
/// materialization of the "UTF-8 byte order". Any cultural or case-insensitive
/// comparator would vary between machines and ICU/NLS versions, violating the byte-
/// by-byte determinism of §3.
/// Path tie is impossible within a tree (paths are unique);
/// the mtime → size → path tie-breaking of ADR-0003 applies to decisions between
/// DISTINCT entries (groups, conflicts) and lives in the pipeline, not in this
/// path comparison.
/// </summary>
public static class PathOrder
{
    /// <summary>Canonical FileEntry comparer by path byte-by-byte.</summary>
    public static readonly IComparer<FileEntry> Comparer =
        Comparer<FileEntry>.Create((a, b) =>
            string.CompareOrdinal(a.Path, b.Path));

    /// <summary>Sorts any collection by the canonical key of an associated FileEntry.</summary>
    public static IOrderedEnumerable<T> Sort<T>(
        IEnumerable<T> items,
        Func<T, FileEntry> key) => items.OrderBy(key, Comparer);
}
