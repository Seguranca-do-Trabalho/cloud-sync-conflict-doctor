namespace Doctor.Core;

/// <summary>
/// Placeholder report record (schema-report-v1.md §6.3): path,
/// labels in declared canonical order and size obtained from enumeration
/// metadata — never by opening the file.
/// </summary>
public sealed record PlaceholderRecord
{
    /// <summary>Relative path, global sort by bytes.</summary>
    public required string Path { get; init; }

    /// <summary>Detected labels, no duplicates, in canonical order:
    /// reparse_point → recall_on_data_access → recall_on_open → offline.
    /// Allowed values: only these four.</summary>
    public required IReadOnlyList<string> Kinds { get; init; }

    /// <summary>Size seen at Level 0, without opening the file.</summary>
    public required long SizeBytes { get; init; }
}

/// <summary>
/// T09 — projection of Level 0 entries to the Placeholders[] list in the report.
/// Placeholders stay outside L1/L2/L3 by construction: the pipeline consumes them
/// from this list and never delivers them to the hasher (gate: <see cref="PlaceholderGuardedHasher"/>).
/// Telemetry: FilesPlaceholder counts the entries; PlaceholderBytesRead remains 0 —
/// security invariant (SPEC §21).
/// </summary>
public static class PlaceholderReport
{
    /// <summary>Canonical label order declared in schema-report-v1.md §6.3.</summary>
    private static readonly (FileAttributes Bit, string Label)[] CanonicalOrder =
    {
        (FileAttributes.ReparsePoint, "reparse_point"),
        (PlaceholderPolicy.RecallOnDataAccess, "recall_on_data_access"),
        (PlaceholderPolicy.RecallOnOpen, "recall_on_open"),
        (FileAttributes.Offline, "offline"),
    };

    /// <summary>
    /// Projects EVERY IsPlaceholder entry as a <see cref="PlaceholderRecord"/>,
    /// sorted by path bytes (StringComparer.Ordinal on the path).
    /// </summary>
    public static IReadOnlyList<PlaceholderRecord> Records(IEnumerable<FileEntry> files) => files
        .Where(f => f.IsPlaceholder)
        .Select(f => new PlaceholderRecord { Path = f.Path, Kinds = Kinds(f), SizeBytes = f.Size })
        .OrderBy(r => r.Path, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Labels without duplicates, in the schema's declared canonical order. Dual source,
    /// because the reason can come from two places: raw bits (native Windows) or
    /// classification already done at origin (<see cref="FileEntry.PlaceholderKind"/> —
    /// includes T04 sidecar simulation).
    /// </summary>
    private static IReadOnlyList<string> Kinds(FileEntry entry)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (bit, label) in CanonicalOrder)
        {
            if ((entry.Attributes & bit) == bit)
            {
                selected.Add(label);
            }
        }

        if (entry.PlaceholderKind is { } kind)
        {
            selected.Add(Label(kind));
        }

        return CanonicalOrder
            .Where(line => selected.Contains(line.Label))
            .Select(line => line.Label)
            .ToArray();
    }

    private static string Label(PlaceholderKind kind) => kind switch
    {
        PlaceholderKind.Offline => "offline",
        PlaceholderKind.RecallOnOpen => "recall_on_open",
        PlaceholderKind.RecallOnDataAccess => "recall_on_data_access",
        _ => "reparse_point",
    };
}
