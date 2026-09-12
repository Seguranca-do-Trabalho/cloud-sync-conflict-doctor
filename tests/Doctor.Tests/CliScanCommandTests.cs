namespace Doctor.Tests;

using System.Text.Json;
using Doctor.Cli;
using Doctor.Core;

/// <summary>
/// T16 (t_71afe316) — CLI 'conflictdoctor scan <path> [--json] [--quiet]' (SPEC §14,
/// EPIC 09). In-process invocation via ScanCommand.Run with tree in tmp covering
/// 4 documented exit codes + structural validity of JSON v1. The CLI is a thin layer:
/// composes production pipeline exactly as card T12 defined it
/// (Sidecar → Ordered → CrossPlatform; Blake3Hasher), formats and translates verdict.
///
/// [Collection] serializes with SecuritySeg12Tests because both call ScanCommand.Run
/// which uses DefaultEnumeration (mutable static); WithSimulatedAccessError temporarily
/// replaces this property, and parallel execution would inject synthetic errors.
/// </summary>
[Collection("ScanCommand")]
public sealed class CliScanCommandTests : IDisposable
{
    private const int Kib = 1024;

    // > 128 KiB: partial hash = windows [0,64K)+[end-64K,end); distinct cores force
    // partial collision with divergent full hash ⇒ real conflict REQUIRES Level 3.
    private const int ConflictFileSize = 200 * Kib;
    private const int WindowSize = 64 * Kib;

    private readonly string _root;

    public CliScanCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"t16-cli-{Guid.NewGuid():N}");
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
            // best-effort cleanup: OS tmp reclaims later (suite pattern)
        }
    }

    // ---- EXIT 0 — clean tree -------------------------------------------------------

    [Fact]
    public void CleanTree_WithoutJson_WithoutQuiet_ExitZero_SummaryText()
    {
        var before = new HashSet<string>(Directory.EnumerateFileSystemEntries(_root));
        File.WriteAllText(MakePath("readme.txt"), "single file, no pair");
        var after = new HashSet<string>(Directory.EnumerateFileSystemEntries(_root));

        var r = ScanCommand.Run(new[] { "scan", _root });

        Assert.Equal(0, r.ExitCode);
        Assert.Null(r.JsonOutput);
        Assert.NotNull(r.HumanText);
        Assert.DoesNotContain("duplicat", r.HumanText, StringComparison.OrdinalIgnoreCase);
        // NEVER delete: CLI does not touch user content — neither reads beyond list,
        // nor writes anything back (no cache/sidecar created inside scanned root).
        Assert.Equal(after, new HashSet<string>(Directory.EnumerateFileSystemEntries(_root)));
    }

    [Fact]
    public void CleanTree_WithJson_ExitZero_ValidJsonWithoutAnomalies()
    {
        File.WriteAllText(MakePath("single.dat"), "sample content");

        var r = ScanCommand.Run(new[] { "scan", _root, "--json" });

        Assert.Equal(0, r.ExitCode);
        Assert.Null(r.HumanText);
        var doc = JsonDocument.Parse(r.JsonOutput!); // Valid JSON (current schema)
        // Schema v2 (SEG-12/t_694bc7ce): files_excluded_conflictdoctor added to
        // telemetry ⇒ bump 1→2 per §7.1 of schema-report-v1 (additive change = bump).
        Assert.Equal(2, doc.RootElement.GetProperty("report_schema_version").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("identical_duplicates").EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("real_conflicts").EnumerateArray());
    }

    // ---- EXIT 2 — identical duplicates ----------------------------------------------

    [Fact]
    public void IdenticalDuplicates_WithoutFlags_ExitTwo_ListedInText()
    {
        var content = "pair of identical duplicates for exit code 2";
        // Same normalized BASE NAME in distinct directories: SPEC §7 grouping
        // is by (normalized_base_name, size) — distinct names are never duplicates.
        Directory.CreateDirectory(MakePath("docs"));
        File.WriteAllText(MakePath("docs/photo.txt"), content);
        Directory.CreateDirectory(Path.Combine(_root, "docs", "backup"));
        File.WriteAllText(MakePath("docs/backup/photo.txt"), content);

        var r = ScanCommand.Run(new[] { "scan", _root });

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("photo.txt", r.HumanText);
        Assert.DoesNotContain("PARTIAL", r.HumanText);
    }

    [Fact]
    public void IdenticalDuplicates_WithJson_ExitTwo_JsonWithDuplicates()
    {
        var content = new byte[64];
        Random.Shared.NextBytes(content);
        Directory.CreateDirectory(MakePath("a"));
        Directory.CreateDirectory(MakePath("b"));
        File.WriteAllBytes(MakePath("a/data.bin"), content);
        File.WriteAllBytes(MakePath("b/data.bin"), content);

        var r = ScanCommand.Run(new[] { "scan", _root, "--json" });

        Assert.Equal(2, r.ExitCode);
        var doc = JsonDocument.Parse(r.JsonOutput!);
        var dup = doc.RootElement.GetProperty("identical_duplicates");
        Assert.Single(dup.EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("real_conflicts").EnumerateArray());
        // BLAKE3 lowercase hex 32 bytes (ADR-0005 §1) — 64 characters.
        var hash = dup[0].GetProperty("hash").GetString();
        Assert.Equal(64, hash!.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    // ---- EXIT 2 — real conflict requires L3 (same size, same windows, distinct core)

    [Fact]
    public void RealConflict_RequiresLevel3_ExitTwo_JsonWithConflict()
    {
        File.WriteAllBytes(MakePath("budget.xlsx"), ConflictContent(0x11));
        File.WriteAllBytes(MakePath("budget-DESKTOP-ABC123.xlsx"), ConflictContent(0x22));

        var r = ScanCommand.Run(new[] { "scan", _root, "--json" });

        Assert.Equal(2, r.ExitCode);
        var doc = JsonDocument.Parse(r.JsonOutput!);
        var conflicts = doc.RootElement.GetProperty("real_conflicts");
        Assert.Single(conflicts.EnumerateArray());
        Assert.Equal("budget.xlsx", conflicts[0].GetProperty("normalized_base_name").GetString());
        Assert.Equal(2, conflicts[0].GetProperty("files").GetArrayLength());
        Assert.NotEqual(
            conflicts[0].GetProperty("files")[0].GetProperty("hash").GetString(),
            conflicts[0].GetProperty("files")[1].GetProperty("hash").GetString());
    }

    // ---- EXIT 3 — partial: anomalies AND skipped files -----------------------------

    [Fact]
    public void AnomaliesWithSkippedFiles_ExitThree_MarksPartial()
    {
        var content = "content shared between both copies of pair";
        File.WriteAllText(MakePath("doc.txt"), content);
        Directory.CreateDirectory(MakePath("copy"));
        File.WriteAllText(MakePath("copy/doc.txt"), content);

        // Individual access error injected at CLI boundary (contracts.md R10:
        // error does not abort scan; partial verdict is decided by CLI LAYER).
        WithSimulatedAccessError(() =>
        {
            var r = ScanCommand.Run(new[] { "scan", _root });
            Assert.Equal(3, r.ExitCode);
            Assert.Contains("doc.txt", r.HumanText);        // listed anomaly
            Assert.Contains("untouchable.bin", r.HumanText); // listed skipped
            Assert.Contains("PARTIAL", r.HumanText);
        });
    }

    [Fact]
    public void ErrorsWithoutAnomalies_RemainsExitZero()
    {
        // Partial (3) requires anomaly AND skipped; error without anomalies stays 0.
        File.WriteAllText(MakePath("single-no-pair.txt"), "solitary file");

        WithSimulatedAccessError(() =>
        {
            var r = ScanCommand.Run(new[] { "scan", _root });
            Assert.Equal(0, r.ExitCode);
            Assert.Contains("untouchable.bin", r.HumanText); // evidence of skipped file
        });
    }

    /// <summary>Injects synthetic ScanError on real L0 and restores at end.</summary>
    private void WithSimulatedAccessError(Action proof)
    {
        var original = ScanCommand.DefaultEnumeration;
        ScanCommand.DefaultEnumeration = root =>
        {
            var result = original(root);
            var withError = result.Errors.Append(
                new ScanError(Path.Combine(_root, "forbidden", "untouchable.bin"), "permission denied (simulated)")).ToArray();
            return result with { Errors = withError };
        };

        try
        {
            proof();
        }
        finally
        {
            ScanCommand.DefaultEnumeration = original;
        }
    }

    // ---- EXIT 1 — operational errors -------------------------------------------------

    [Fact]
    public void InvalidUsage_ExitOne_MessageOnStderr()
    {
        Assert.Equal(1, ScanCommand.Run(new[] { "scan" }).ExitCode);
        Assert.Equal(1, ScanCommand.Run(Array.Empty<string>()).ExitCode);
        Assert.Equal(1, ScanCommand.Run(new[] { "unknown-command", "/tmp" }).ExitCode);
        Assert.Equal(1, ScanCommand.Run(new[] { "scan", _root, "--nonexistent-flag" }).ExitCode);
    }

    [Fact]
    public void NonexistentRoot_ExitOne_OperationalMessage()
    {
        var ghost = Path.Combine(_root, "nonexistent");

        var r = ScanCommand.Run(new[] { "scan", ghost });

        Assert.Equal(1, r.ExitCode);
        Assert.Null(r.JsonOutput);
        Assert.Contains(ghost, r.HumanText);
    }

    [Fact]
    public void RootIsFile_NotDirectory_ExitOne()
    {
        var file = MakePath("a-file.txt");
        File.WriteAllText(file, "not a scan root");

        Assert.Equal(1, ScanCommand.Run(new[] { "scan", file }).ExitCode);
    }

    // ---- --quiet ----------------------------------------------------------------------

    [Fact]
    public void Quiet_Duplicates_ExitTwo_EmptyText()
    {
        var content = "same content in two copies for quiet";
        Directory.CreateDirectory(MakePath("q1"));
        Directory.CreateDirectory(MakePath("q2"));
        File.WriteAllText(MakePath("q1/x.txt"), content);
        File.WriteAllText(MakePath("q2/x.txt"), content);

        var r = ScanCommand.Run(new[] { "scan", _root, "--quiet" });

        Assert.Equal(2, r.ExitCode);
        Assert.Null(r.JsonOutput);
        Assert.Equal(string.Empty, r.HumanText); // silent even with anomaly
    }

    // ---- CLI report determinism (ADR-0003 minimal CI rule) ---------------

    [Fact]
    public void Json_TwoRunsOnSameTree_ByteIdentical()
    {
        var content = "json determinism emitted by cli";
        Directory.CreateDirectory(MakePath("d1"));
        Directory.CreateDirectory(MakePath("d2"));
        File.WriteAllText(MakePath("d1/p.txt"), content);
        File.WriteAllText(MakePath("d2/p.txt"), content);

        var r1 = ScanCommand.Run(new[] { "scan", _root, "--json" });
        var r2 = ScanCommand.Run(new[] { "scan", _root, "--json" });

        // ADR-0003: timestamps live ONLY in generated_from — masked, everything else
        // in report v1 must be byte-identical between runs.
        Assert.Equal(
            MaskTimestamps(r1.JsonOutput!),
            MaskTimestamps(r2.JsonOutput!));
    }

    /// <summary>Wall clock fields of schema v1 (ADR-0003): vary per
    /// execution; everything else is canonical. Same pattern as T13 (ReportWriterTests).</summary>
    private static string MaskTimestamps(string json) =>
        System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"(scan_started_utc|scan_finished_utc)\": \"[^\"]*\"",
            "$1: \"MASKED\"");

    [Fact]
    public void PlaceholderNeverRead_CountsAsNonAnomaly()
    {
        // Placeholders enter §6.3 list but are NOT a conflict/duplicate anomaly.
        var target = MakePath("offline.bin");
        File.WriteAllBytes(target, new byte[4096]);
        File.WriteAllText(target + ".placeholder-meta.json", "{\"kind\":\"offline\"}");

        var r = ScanCommand.Run(new[] { "scan", _root, "--json" });

        Assert.Equal(0, r.ExitCode); // isolated placeholder does not become exit 2/3
        var doc = JsonDocument.Parse(r.JsonOutput!);
        var ph = doc.RootElement.GetProperty("placeholders");
        Assert.Single(ph.EnumerateArray());
        Assert.Equal(0, doc.RootElement.GetProperty("telemetry").GetProperty("placeholder_bytes_read").GetInt64());
    }

    // ---- fixtures ---------------------------------------------------------------------

    private string MakePath(string relative)
    {
        var parts = relative.Split('/');
        return Path.Combine(new[] { _root }.Concat(parts).ToArray());
    }

    /// <summary>Fixed head and tail (guaranteed L2 partial collision); varying core —
    /// same pattern as T12 DET-03 test.</summary>
    private static byte[] ConflictContent(byte core)
    {
        var bytes = new byte[ConflictFileSize];

        for (var i = 0; i < WindowSize; i++)
        {
            bytes[i] = (byte)(0xAA + (i % 13));
        }

        for (var i = ConflictFileSize - WindowSize; i < ConflictFileSize; i++)
        {
            bytes[i] = (byte)(0xBB + (i % 17));
        }

        for (var i = WindowSize; i < ConflictFileSize - WindowSize; i++)
        {
            bytes[i] = core;
        }

        return bytes;
    }
}
