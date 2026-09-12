namespace Doctor.Tests;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Doctor.Cli;
using Doctor.Core;

/// <summary>
/// SEG-12 (t_694bc7ce; addendum T-15 from security-audit-gate5; case T-06 from threat-model,
/// rule R1): quarantine publishes inside &lt;root&gt;/ConflictDoctor/quarantine/&lt;op_id&gt;/
/// INSIDE the scanned root (ADR-0002/SPEC §18), so a second scan over the same root
/// would find the .dat payloads as candidates — polluting the report and breaking
/// idempotency §20. Level 0 enumeration excludes the reserved subtree by comparing
/// the canonical path PREFIX IN BYTES and counts excluded entries in
/// files_excluded_conflictdoctor (schema v2, §5/§7.1 of schema-report-v1).
///
/// Flow exercised is the full production: ScanCommand.Run composes
/// SidecarPlaceholderEnumerator(OrderedFileEnumerator(CrossPlatformEnumerator)) +
/// ScanPipeline L0→L3 + ReportWriterJson — no test pieces in the path.
/// NEVER File.Delete: quarantine is born and persists (ADR-0002).
/// </summary>
[Collection("ScanCommand")]
public sealed class SecuritySeg12Tests : IDisposable
{
    private readonly string _root;

    public SecuritySeg12Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "seg12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup: OS temp will reclaim later
        }
    }

    [Fact]
    public void Security_QuarantineInsideScannedRoot_ExcludedFromEnumeration()
    {
        // ---- initial tree: identical pair + single file -----------------------------
        var pairContent = "identical pair content - seg12 - single version";
        Directory.CreateDirectory(MakePath("docs", "backup"));
        File.WriteAllText(MakePath("docs", "foto.txt"), pairContent);
        File.WriteAllText(MakePath("docs", "backup", "foto.txt"), pairContent);
        File.WriteAllText(MakePath("readme.txt"), "single file\n");

        // Scan 1 (pre-resolution): the pair appears as duplicate (exit 2) — sanity check.
        var scan1 = ScanCommand.Run(["scan", _root, "--json"]);
        Assert.Equal(2, scan1.ExitCode);
        Assert.Contains("docs/backup/foto.txt", scan1.JsonOutput, StringComparison.Ordinal);

        // ---- REAL resolution: move the duplicate to quarantine §18 (inside root) --
        var enumerator = new SidecarPlaceholderEnumerator(
            new OrderedFileEnumerator(new CrossPlatformEnumerator()));
        var snapshot = enumerator.Enumerate(_root);
        var duplicate = snapshot.Files.Single(e => e.Path == MakePath("docs", "backup", "foto.txt"));

        var operation = new QuarantineService().Move(
            [new QuarantineItem(duplicate, "IDENTICAL_DUPLICATE", "KEEP_NEWEST")],
            new QuarantinePlan(_root, new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero)));

        // Pre-condition of case T-06: quarantine was published INSIDE the scanned root.
        Assert.True(Directory.Exists(Path.Combine(
            _root, "ConflictDoctor", "quarantine", operation.OperationId)));
        // .dat payload + manifest physically exist inside the scanned tree.
        Assert.True(File.Exists(Path.Combine(
            _root, "ConflictDoctor", "quarantine", operation.OperationId, "payload", "0001.dat")));
        Assert.True(File.Exists(Path.Combine(
            _root, "ConflictDoctor", "quarantine", operation.OperationId, "manifest.json")));

        // ---- two rescans of the SAME tree post-resolution --------------------------------
        var scan2 = ScanCommand.Run(["scan", _root, "--json"]);
        var scan3 = ScanCommand.Run(["scan", _root, "--json"]);

        Assert.Equal(0, scan2.ExitCode); // duplicate resolved: tree clean for the product
        Assert.Equal(0, scan3.ExitCode);

        // 1. ZERO ConflictDoctor/ items in report — no payload, no manifest,
        //    no entry under the reserved subtree (paths are relative to root).
        Assert.DoesNotContain("ConflictDoctor", scan2.JsonOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ConflictDoctor", scan3.JsonOutput, StringComparison.Ordinal);

        // 2. Dedicated counter > 0: .dat payload + manifest.json were excluded and COUNTED.
        using var doc2 = JsonDocument.Parse(scan2.JsonOutput!);
        var excluded = doc2.RootElement
            .GetProperty("telemetry")
            .GetProperty("files_excluded_conflictdoctor")
            .GetInt64();
        Assert.True(excluded >= 2, $"expected >= 2 excluded entries, got {excluded}");

        // 3. Idempotency §20: second scan byte-identical to third (mask only
        //    wall timestamps, condition §1.6 of schema-report-v1).
        Assert.Equal(Mask(scan2.JsonOutput!), Mask(scan3.JsonOutput!));

        // Exclusion is policy, not error: nothing becomes ScanError (contracts.md R10).
        var post = enumerator.Enumerate(_root);
        Assert.Empty(post.Errors);
        Assert.Equal(excluded, post.Telemetry.FilesExcludedConflictDoctor);
    }

    [Fact]
    public void Security_TreeWithoutConflictDoctor_CounterZero_OutputUnchanged()
    {
        // Tree WITHOUT reserved subtree: identical pair with SAME base name (grouping
        // SPEC §7 is by normalized_base_name + size — distinct names are never
        // duplicates; correction from previous run of this card) + single, common composition.
        var content = "simple pair without quarantine";
        Directory.CreateDirectory(MakePath("data", "backup"));
        File.WriteAllText(MakePath("data", "a.txt"), content);
        File.WriteAllText(MakePath("data", "backup", "a.txt"), content);
        File.WriteAllText(MakePath("root.txt"), "single\n");

        var scanA = ScanCommand.Run(["scan", _root, "--json"]);
        var scanB = ScanCommand.Run(["scan", _root, "--json"]);

        Assert.Equal(2, scanA.ExitCode); // the pair continues to be reported — output unchanged

        using var docA = JsonDocument.Parse(scanA.JsonOutput!);
        using var docB = JsonDocument.Parse(scanB.JsonOutput!);

        // Counter zeroed when there is nothing to exclude.
        Assert.Equal(0, docA.RootElement.GetProperty("telemetry").GetProperty("files_excluded_conflictdoctor").GetInt64());
        Assert.Equal(0, docB.RootElement.GetProperty("telemetry").GetProperty("files_excluded_conflictdoctor").GetInt64());

        // Enumerated = exactly the 3 created files (no more, no less).
        Assert.Equal(3, docA.RootElement.GetProperty("telemetry").GetProperty("files_enumerated").GetInt64());

        // Determinism preserved: repeated scans remain byte-identical (§20).
        Assert.Equal(Mask(scanA.JsonOutput!), Mask(scanB.JsonOutput!));

        // The duplicate remains visible in the report — exclusion does not reach user content.
        Assert.Contains("data/backup/a.txt", scanA.JsonOutput!, StringComparison.Ordinal);
    }

    private string MakePath(params string[] segments)
    {
        var all = new List<string> { _root };
        all.AddRange(segments);
        return Path.Combine(all.ToArray());
    }

    /// <summary>Mask §1.6 of schema-report-v1: only wall timestamps vary between
    /// real scans; everything else must match byte for byte.</summary>
    private static string Mask(string json) => Regex.Replace(
        json,
        "\"scan_(started|finished)_utc\": \"[^\"]+\"",
        "\"scan_$1_utc\": \"MASKED-FOR-DETERMINISM-TEST\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));
}
