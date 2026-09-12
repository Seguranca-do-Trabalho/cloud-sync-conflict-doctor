namespace Doctor.Core;

using System.Diagnostics;

// ScanTelemetry moved: single source in Telemetry.cs (T06 contract,
// SPEC §10 complete + placeholder_bytes_read gate). This file keeps only
// Level 0 enumeration types.
/// <summary>
/// Individual scan error (permission, long path, etc.). Never aborts the scan:
/// becomes a record in the report — no silent failure (docs/contracts.md, risk R10).
/// </summary>
[DebuggerDisplay("{" + nameof(ToString) + "(),nq}")]
public sealed record ScanError(string Path, string Message);

/// <summary>
/// Level 0 enumeration result (docs/contracts.md): <see cref="Files"/> is ALWAYS
/// sorted by canonical order (<see cref="PathOrder"/>) — independent of the physical
/// filesystem order; individual errors never abort the scan.
/// </summary>
public sealed record EnumerationResult(
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<ScanError> Errors,
    ScanTelemetry Telemetry);

/// <summary>
/// Level 0 enumeration contract — metadata only, zero content reading
/// (SPEC §5; docs/contracts.md). Implementors may scan in any physical order;
/// never cross directory reparse points; never open placeholders (SPEC §6).
/// </summary>
public interface IFileEnumerator
{
    EnumerationResult Enumerate(string rootPath, CancellationToken ct = default);
}

/// <summary>
/// Deterministic wrapper over any physical enumerator (SPEC §3):
/// reorders by the byte-by-byte canonical path order (<see cref="PathOrder"/>),
/// marks placeholders via <see cref="PlaceholderPolicy"/>, applies <see cref="ReparsePolicy"/>
/// (IsReparsePoint marking, per-inode visited guard and depth ceiling —
/// threat-model T-02) and derives telemetry from the final sorted list.
/// Does not read content — delegates only metadata to the inner enumerator.
/// Policy rejections become <see cref="ScanError"/> in <see cref="EnumerationResult.Errors"/>:
/// never silent failure (contracts.md R10).
/// </summary>
public sealed class OrderedFileEnumerator : IFileEnumerator
{
    private readonly IFileEnumerator _inner;
    private readonly ReparsePolicy _reparsePolicy;

    public OrderedFileEnumerator(IFileEnumerator inner, ReparsePolicy? reparsePolicy = null)
    {
        _inner = inner;
        _reparsePolicy = reparsePolicy ?? new ReparsePolicy();
    }

    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var physical = _inner.Enumerate(rootPath, ct);

        // ---- canonical ordering + markings (physical order never decides; §3) -------------
        var ordered = PathOrder.Sort(physical.Files, e => e)
            .Select(e => e with
            {
                IsPlaceholder = PlaceholderPolicy.IsPlaceholder(e),
                PlaceholderKind = PlaceholderPolicy.Classify(e),
                IsReparsePoint = e.IsReparsePoint || (e.Attributes & FileAttributes.ReparsePoint) != 0,
            })
            .ToArray();

        // ---- ReparsePolicy: loop detection by inode + depth ceiling --------------
        // Per-inode visited guard on the ALREADY sorted list: deterministic decision
        // — always keeps the FIRST occurrence in canonical order, regardless of
        // physical arrival order (T-02 mitigation; contracts.md §3).
        var visited = new HashSet<(string VolumeId, string FileId)>();
        var files = new List<FileEntry>(ordered.Length);
        var errors = new List<ScanError>(physical.Errors);
        var normalizedRoot = rootPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

        // ---- SEG-12 (T-15 addendum; threat-model T-06/R1): reserved subtree outside enumeration --
        // <root>/ConflictDoctor/ is the tool's territory (quarantine §18/SPEC,
        // ADR-0002): any entry under this prefix is structural policy of the
        // Level 0 canonical boundary — never a candidate, never an error (contracts.md R10),
        // never counted in files_enumerated. PREFIX-BYTES comparison on the
        // canonical path (Ordinal), separator already normalized on both sides.
        var reservedPrefix = normalizedRoot
            + Path.DirectorySeparatorChar
            + ReservedSubtree.DirectoryName
            + Path.DirectorySeparatorChar;

        var excludedReserved = 0;

        foreach (var entry in ordered)
        {
            var canonicalPath = entry.Path.Replace(
                Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            if (canonicalPath.StartsWith(reservedPrefix, StringComparison.Ordinal))
            {
                excludedReserved++;
                continue;
            }

            if (!visited.Add((entry.VolumeId, entry.FileId)))
            {
                errors.Add(new ScanError(
                    entry.Path,
                    "reparse: path returns to the same already-visited inode — entry rejected (loop detection, threat-model T-02)"));
                continue;
            }

            var depth = GetDepth(entry.Path, normalizedRoot);
            if (depth > _reparsePolicy.MaxDepth)
            {
                errors.Add(new ScanError(
                    entry.Path,
                    $"reparse: depth {depth} exceeds ceiling of {_reparsePolicy.MaxDepth} — entry rejected (threat-model T-02)"));
                continue;
            }

            files.Add(entry);
        }

        var telemetry = physical.Telemetry with
        {
            FilesEnumerated = files.Count,
            FilesPlaceholder = files.Count(f => f.IsPlaceholder),
            // Cumulative across layers: production composition suffers double wrap
            // (pipeline → Ordered over Ordered+CrossPlatform) and the inner layer already
            // excluded; recounting would zero the counter (idempotency §20).
            FilesExcludedConflictDoctor =
                physical.Telemetry.FilesExcludedConflictDoctor + excludedReserved,
        };

        return new EnumerationResult(files, errors, telemetry);
    }

    /// <summary>Relative depth from root in directory segments (root = 0).</summary>
    private static int GetDepth(string path, string normalizedRoot)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (!normalized.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            return 0; // path outside root has no measurable relative depth
        }

        var relative = normalized.AsSpan(normalizedRoot.Length).TrimStart(Path.DirectorySeparatorChar);
        if (relative.IsEmpty)
        {
            return 0;
        }

        var depth = 1; // the first segment after root is the file itself
        foreach (var c in relative)
        {
            if (c == Path.DirectorySeparatorChar)
            {
                depth++;
            }
        }

        return depth;
    }
}
