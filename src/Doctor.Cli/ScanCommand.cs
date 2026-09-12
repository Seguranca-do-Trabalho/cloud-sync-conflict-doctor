namespace Doctor.Cli;

using System.Globalization;
using Doctor.Core;

/// <summary>
/// T16 — testable result of the scan command execution (skill
/// tdd-deterministic-tooling: CLI as a thin layer, run(argv) -> (exit_code, text);
/// terminal is never needed for testing).
/// </summary>
/// <param name="ExitCode">
/// 0 no anomalies, 1 operational error, 2 duplicates/conflicts, 3 partial (SPEC §14).
/// </param>
/// <param name="JsonOutput">Full v1 JSON when --json; null in other modes.</param>
/// <param name="HumanText">Human text (--quiet: empty); null in JSON mode.</param>
public sealed record CliResult(int ExitCode, string? JsonOutput, string? HumanText);

/// <summary>
/// Command 'conflictdoctor scan &lt;path&gt; [--json] [--quiet]' over the existing
/// pipeline (ScanPipeline L0→L3 + ReportWriterJson v1). No new domain decisions:
/// the CLI only composes, formats, and translates the verdict into a stable exit code.
///
/// Contracts respected:
/// - ADR-0002: no deletion API — the CLI only reads and writes the report;
/// - ADR-0003: deterministic report; timestamps only in generated_from;
/// - contracts.md R10: individual access error does not abort the scan; with anomaly
///   AND skipped files the verdict is partial (exit 3), otherwise 0/2;
/// - fail-closed: any domain exception becomes exit 1 with a single message,
///   never a raw stack trace.
/// </summary>
public static class ScanCommand
{
    /// <summary>
    /// Production L0 source (T12/T13): sidecar convention (T04) over canonical ordering
    /// over cross-platform physical enumeration. Injectable for tests.
    /// </summary>
    public static Func<string, EnumerationResult> DefaultEnumeration { get; set; } =
        root => new SidecarPlaceholderEnumerator(
            new OrderedFileEnumerator(PlatformEnumerator())).Enumerate(root);

    /// <summary>
    /// Selects the Level 0 enumerator based on the operating system.
    ///
    /// Production always instantiated CrossPlatformEnumerator, even though
    /// WindowsNativeEnumerator exists specifically for Windows (ADR-0004,
    /// card T23). This affected much more than performance:
    ///
    /// CrossPlatformEnumerator obtains the file id via lstat(2) — a P/Invoke of
    /// libc that only exists on POSIX. On Windows it has no source for a real id
    /// and returns "0" for ALL files. Since OrderedFileEnumerator uses the
    /// (VolumeId, FileId) key to detect reparse point cycles, the first file
    /// entered and all subsequent ones were rejected as "path loops back to
    /// the same inode already visited". In practice, a scan on Windows
    /// saw only one file per volume.
    ///
    /// The native enumerator obtains the real 128-bit NTFS FileId
    /// (GetFileInformationByHandleEx / FILE_ID_INFO) and the volume serial
    /// number, which is the correct pair for this detection.
    /// </summary>
    internal static IFileEnumerator PlatformEnumerator() =>
        OperatingSystem.IsWindows()
            ? new WindowsNativeEnumerator()
            : new CrossPlatformEnumerator();

    public static CliResult Run(string[] args)
    {
        // ---- closed grammar before any use (skill tdd-deterministic-tooling)
        // 'scan' is a mandatory subcommand consumed explicitly (T16).
        if (args.Length == 0 || !string.Equals(args[0], "scan", StringComparison.Ordinal))
        {
            return Operational("invalid usage: unknown command. syntax: conflictdoctor scan <path> --json --quiet");
        }

        var json = false;
        var quiet = false;
        string? root = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "--json", StringComparison.Ordinal))
            {
                json = true;
            }
            else if (string.Equals(arg, "--quiet", StringComparison.Ordinal))
            {
                quiet = true;
            }
            else if (!string.Equals(arg, "scan", StringComparison.Ordinal)
                     && root is null
                     && !arg.StartsWith('-'))
            {
                root = arg;
            }
            else
            {
                return Operational($"invalid usage: '{arg}'. syntax: conflictdoctor scan <path> --json --quiet");
            }
        }

        if (root is null)
        {
            return Operational("invalid usage: path required. syntax: conflictdoctor scan <path> --json --quiet");
        }

        if (!Directory.Exists(root))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(root));
            return Operational(Directory.Exists(parent)
                ? $"root is not a directory: {root}"
                : $"root does not exist: {root}");
        }

        try
        {
            return ExecuteScan(root, json, quiet);
        }
        catch (OperationCanceledException)
        {
            return Operational("scan cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlaceholderViolationException or PlaceholderReadException)
        {
            // Fail-closed: operational error with a single message — nothing partial without a record.
            return Operational($"scan failed: {ex.Message}");
        }
    }

    private static CliResult ExecuteScan(string root, bool json, bool quiet)
    {
        var started = DateTimeOffset.UtcNow;

        // ---- single L0 (sidecar-aware): source of truth for files, errors and placeholders
        var l0 = DefaultEnumeration(root);

        // ---- L1→L3 over the SAME already-marked and ordered list ---------------------------
        // The pipeline reorders internally (structural defense) and its projection queries
        // PlaceholderPolicy.Classify — hardened in T16 to treat marking done at the
        // SOURCE (sidecar T04 today, native hook tomorrow) as the ultimate authority. This way
        // the simulated placeholder mark survives the internal wrap and NO placeholder
        // byte is read (PLH-01), without artificial bit translation.
        var result = new ScanPipeline(
            new ReplayMarkedEnumerator(l0.Files),
            new PlaceholderGuardedHasher(new Blake3Hasher()),
            new FileStreamSource()).Run(root);

        // ---- exact telemetry: counters derived from recorded decisions ------------
        // Partial (L2) ran for every group member with 2+ items; full (L3) only
        // for members of groups with a verdict. Byte recipe from ADR-0005 via
        // hasher constants (never loose numbers).
        var membersL2 = result.Groups
            .Where(g => g.Members.Count >= 2)
            .SelectMany(g => g.Members)
            .ToArray();
        // L3 covers ALL members of groups with a verdict; the size of a conflict
        // member is the group size (grouping is by size — Grouping.Group).
        var sizesL3 = result.IdenticalDuplicates
            .SelectMany(d => d.Files.Select(f => f.Size))
            .Concat(result.RealConflicts.SelectMany(c => c.Files.Select(_ => c.SizeBytes)))
            .ToArray();

        var telemetry = l0.Telemetry with
        {
            FilesSkipped = l0.Errors.Count,
            FilesPartialHashed = membersL2.Length,
            FilesFullHashed = sizesL3.Length,
            BytesReadPartial = membersL2.Sum(PartialBytes),
            BytesReadFull = sizesL3.Sum(),
        };

        var finished = DateTimeOffset.UtcNow;
        var placeholders = PlaceholderReport.Records(l0.Files);

        // ---- verdict in stable exit code (SPEC §14) ------------------------------------
        var anomalies = result.IdenticalDuplicates.Count + result.RealConflicts.Count;
        var skipped = l0.Errors.Count;

        int exitCode;
        if (anomalies > 0 && skipped > 0)
        {
            exitCode = Program.ExitCodes.Partial;
        }
        else if (anomalies > 0)
        {
            exitCode = Program.ExitCodes.AnomaliesFound;
        }
        else
        {
            exitCode = Program.ExitCodes.Clean;
        }

        if (json)
        {
            using var output = new MemoryStream();
            new ReportWriterJson().Write(result, telemetry, placeholders, root, started, finished, output);
            return new CliResult(exitCode, System.Text.Encoding.UTF8.GetString(output.ToArray()), null);
        }

        return new CliResult(exitCode, null, quiet
            ? string.Empty
            : FormatText(result, placeholders, l0.Errors, exitCode));
    }

    /// <summary>ADR-0005 recipe v1: ≤ WholeFileLimitBytes reads the whole file; above,
    /// the two WindowBytes windows in a single open.</summary>
    private static long PartialBytes(FileEntry f) =>
        f.Size <= Blake3Hasher.WholeFileLimitBytes
            ? f.Size
            : 2L * Blake3Hasher.WindowBytes;

    private static CliResult Operational(string message) =>
        new(Program.ExitCodes.OperationalError, null, message);

    private static string FormatText(
        ScanResult r,
        IReadOnlyList<PlaceholderRecord> placeholders,
        IReadOnlyList<ScanError> errors,
        int exitCode)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"scan completed: {r.Groups.Count} candidate group(s)");

        foreach (var dup in r.IdenticalDuplicates)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"identical duplicate [{dup.SizeBytes} B] hash {dup.Hash[..12]}…");
            foreach (var f in dup.Files)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.Path}");
            }
        }

        foreach (var c in r.RealConflicts)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"real conflict: {c.NormalizedBaseName} [{c.SizeBytes} B]");
            foreach (var m in c.Files)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {m.Path} (hash {m.Hash[..12]}…)");
            }
        }

        if (placeholders.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"placeholders listed (not read): {placeholders.Count}");
        }

        if (errors.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"files skipped due to access error: {errors.Count}");
            foreach (var error in errors)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {error.Path} ({error.Message})");
            }
        }

        if (exitCode == Program.ExitCodes.Partial)
        {
            sb.AppendLine("STATUS: PARTIAL — anomalies found and files were skipped.");
        }

        return sb.ToString().TrimEnd();
    }
}
