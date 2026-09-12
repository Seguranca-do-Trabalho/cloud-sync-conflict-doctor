namespace Doctor.Core;

using System.Diagnostics;

/// <summary>
/// Security exception (SPEC §6 "DO NOT TOUCH"; ADR-0005 item 6; threat-model T-03):
/// signals attempt to read/hash placeholder content. Never caught to
/// "proceed anyway" — whoever receives it has a gate bug to fix.
/// </summary>
[DebuggerDisplay("PlaceholderReadException: {" + nameof(EntryPath) + "}")]
public sealed class PlaceholderReadException : InvalidOperationException
{
    public PlaceholderReadException(string entryPath)
        : base($"Placeholder CANNOT be opened or hashed (SPEC §6): {entryPath}")
        => EntryPath = entryPath;

    /// <summary>Path of the refused placeholder — diagnostics and test telemetry.</summary>
    public string EntryPath { get; }
}

/// <summary>
/// PIPELINE GATE violation (SPEC §6/§21 — T09 scope reduced by orchestrator):
/// thrown when code attempts to obtain placeholder content outside the allowed
/// flow, or when received telemetry already carries PlaceholderBytesRead != 0
/// (value != 0 is a security violation, not data — Telemetry.cs). Never caught
/// to "proceed anyway": whoever receives it has a gate bug to fix.
/// </summary>
[DebuggerDisplay("PlaceholderViolationException: {" + nameof(EntryPath) + "}")]
public sealed class PlaceholderViolationException : InvalidOperationException
{
    public PlaceholderViolationException(string? entryPath, string reason)
        : base($"Placeholder gate violation (SPEC §6): {reason} {entryPath ?? "(no path)"}")
        => EntryPath = entryPath;

    /// <summary>Path of the refused entry; null when the violation is telemetry.</summary>
    public string? EntryPath { get; }
}

/// <summary>
/// HARD GATE (SPEC §6/§21; ADR-0004 rule 3; ADR-0005 item 6; threat-model T-03):
/// EVERY product IHasher goes through this decorator. Before any stream opening,
/// the entry is reclassified by <see cref="PlaceholderPolicy"/> — the product's
/// single decision point. Blocks:
/// 1. entries marked IsPlaceholder at Level 0;
/// 2. unmarked entries whose raw attributes reveal placeholder (double gate from
///    R3/T-03 mitigation: missing or stale marking does not pass).
/// Automated consequence: placeholder_bytes_read == 0 (PLH-01/02).
/// </summary>
public sealed class PlaceholderGuardedHasher : IHasher
{
    private readonly IHasher _inner;

    public PlaceholderGuardedHasher(IHasher inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string PartialHash(FileEntry entry, CancellationToken ct = default)
    {
        Enforce(entry);
        return _inner.PartialHash(entry, ct);
    }

    public string FullHash(FileEntry entry, CancellationToken ct = default)
    {
        Enforce(entry);
        return _inner.FullHash(entry, ct);
    }

    private static void Enforce(FileEntry entry)
    {
        // Double gate: Level 0 flag OR any suspicious bit in raw attributes.
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }
    }
}

/// <summary>
/// PIPELINE GATE (T09 — SPEC §6/§21; ADR-0005 item 6): single point through which ALL
/// content access passes after Level 0 enumeration. Guarantees BY CONSTRUCTION that
/// placeholder produces no reading:
/// 1. <see cref="Enforce"/> splits the L0 result into safe flow (non-placeholders,
///    for L1/L2/L3) and the report Placeholders[] list (schema v1 §6.3);
/// 2. <see cref="OpenRead"/> reclassifies each opening via <see cref="PlaceholderPolicy"/>
///    (double gate: L0 marking + raw bits) and throws <see cref="PlaceholderViolationException"/>
///    BEFORE touching the source;
/// 3. refuses already-violated input telemetry — the gate never "cleans" a
///    PlaceholderBytesRead != 0 counter.
/// </summary>
public sealed class PlaceholderGate : IStreamSource
{
    private readonly IStreamSource _streams;

    public PlaceholderGate(IStreamSource streams)
        => _streams = streams ?? throw new ArgumentNullException(nameof(streams));

    /// <summary>
    /// Applies the gate to the enumeration result: returns a partial report with only
    /// safe entries in <c>Files</c>, placeholders projected into
    /// <see cref="PartialReport.Placeholders"/> and derived telemetry with
    /// <c>placeholder_bytes_read == 0</c> guaranteed by construction. Throws
    /// <see cref="PlaceholderViolationException"/> if the entry is already violated.
    /// </summary>
    public PartialReport Enforce(EnumerationResult enumeration)
    {
        ArgumentNullException.ThrowIfNull(enumeration);

        if (enumeration.Telemetry.PlaceholderBytesRead != 0)
            throw new PlaceholderViolationException(null, "input telemetry already violated (placeholder_bytes_read != 0):");

        var files = new List<FileEntry>();
        var placeholders = new List<PlaceholderRecord>();

        foreach (var entry in enumeration.Files)
        {
            // Double gate: Level 0 flag OR any suspicious bit in raw attributes.
            if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
                placeholders.Add(PlaceholderReport.Records(new[] { entry }).Single());
            else
                files.Add(entry);
        }

        return new PartialReport(
            files.ToArray(),
            placeholders.OrderBy(p => p.Path, StringComparer.Ordinal).ToArray(),
            enumeration.Telemetry with
            {
                FilesEnumerated = enumeration.Files.Count,
                FilesPlaceholder = placeholders.Count,
                PlaceholderBytesRead = 0,
            });
    }

    /// <summary>Single legitimate path to content post-L0. Refuses placeholder.</summary>
    public Stream OpenRead(FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
            throw new PlaceholderViolationException(entry.Path, "content opening blocked:");

        return _streams.OpenRead(entry);
    }
}

/// <summary>Partial report produced by the gate: basis for L1/L2/L3 and the final report.</summary>
public sealed record PartialReport(
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<PlaceholderRecord> Placeholders,
    ScanTelemetry Telemetry);
