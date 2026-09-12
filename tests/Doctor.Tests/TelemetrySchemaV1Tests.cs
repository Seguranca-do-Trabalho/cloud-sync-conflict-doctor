using Doctor.Gui.Engine;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// Reading bridge: exposes the report validator over raw telemetry,
/// so violation tests can construct broken counters without an engine.
/// </summary>
internal static class ScanTelemetryValidatorExtensions
{
    internal static IEnumerable<string> InvariantViolations(this ScanTelemetry t) =>
        new ScanReport { Telemetry = t }.ValidateTelemetryInvariants();
}

/// <summary>
/// t_2a116a88 — cycle 1 (RED): GUI telemetry in the shape of schema v1
/// (docs/schema-report-v1.md §5): the 9 exact counters and the 4 invariants.
/// The fake engine produces this DEMO-marked data, with selectable scenarios
/// covering the edge cases requested by the card (nominal, no duplicates,
/// no conflicts, placeholders only).
/// </summary>
public class TelemetrySchemaV1Tests
{
    /// <summary>Normative schema v1 §5 invariants, evaluated over the report.</summary>
    private static string[] InvariantViolationStrings(ScanReport r)
    {
        var violations = r.ValidateTelemetryInvariants().ToArray();

        // Structural guard: the validator exists and evaluates the report itself.
        Assert.NotNull(violations);
        return violations;
    }

    [Fact]
    public void Nominal_scenario_produces_exact_nine_counters_from_demo()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal)
            .Scan(@"C:\Users\demo\OneDrive");

        var t = report.Telemetry;

        // 9 EXACT schema v1 counters, values derived from the DEMO dataset:
        // 2 placeholders + 4 singletons (skipped) + 10 partially hashed
        // (8 that proceeded to L3 + 2 eliminated at L2) = 16 enumerated.
        Assert.Equal(16, t.FilesEnumerated);
        Assert.Equal(2, t.FilesPlaceholder);
        Assert.Equal(4, t.FilesSkipped);
        Assert.Equal(10, t.FilesPartialHashed);
        Assert.Equal(8, t.FilesFullHashed);

        // L2 window: 64 KiB head + tail per large file; files <= 128 KiB
        // count the whole file once in bytes_read_partial.
        // (2×64 KiB) + 3×88,412 + 3×90,240 + 91,077 = 925,089 B.
        Assert.Equal(925_089L, t.BytesReadPartial);
        // L3 pass: whole file of survivors (3 docx + 2 jpg + 3 versions).
        Assert.Equal(8_333_281L, t.BytesReadFull);
        Assert.Equal(t.BytesReadPartial + t.BytesReadFull, t.BytesRead);

        // Absolute rule §6/§7: placeholder is never opened.
        Assert.Equal(0L, t.PlaceholderBytesRead);
    }

    [Fact]
    public void Nominal_scenario_maintains_all_four_schema_invariants()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal)
            .Scan(@"C:\Users\demo\OneDrive");

        Assert.Empty(InvariantViolationStrings(report));
    }

    [Theory]
    [InlineData(FakeScanEngine.ScanScenario.Nominal)]
    [InlineData(FakeScanEngine.ScanScenario.NoDuplicates)]
    [InlineData(FakeScanEngine.ScanScenario.NoConflicts)]
    [InlineData(FakeScanEngine.ScanScenario.PlaceholdersOnly)]
    public void Every_scenario_maintains_telemetry_invariants(
        FakeScanEngine.ScanScenario scenario)
    {
        var report = new FakeScanEngine(scenario).Scan(@"C:\demo");

        Assert.Empty(report.ValidateTelemetryInvariants());

        var t = report.Telemetry;
        Assert.Equal(0L, t.PlaceholderBytesRead);
        Assert.Equal(t.BytesReadPartial + t.BytesReadFull, t.BytesRead);
        Assert.True(t.FilesFullHashed <= t.FilesPartialHashed);
    }

    [Fact]
    public void NoDuplicates_scenario_has_no_identical_group_and_summary_zeros()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.NoDuplicates)
            .Scan(@"C:\demo");

        Assert.Empty(report.IdenticalDuplicates);
        Assert.NotEmpty(report.RealConflicts);
    }

    [Fact]
    public void NoConflicts_scenario_has_no_divergence_and_summary_zeros()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.NoConflicts)
            .Scan(@"C:\demo");

        Assert.Empty(report.RealConflicts);
        Assert.NotEmpty(report.IdenticalDuplicates);
    }

    [Fact]
    public void PlaceholdersOnly_scenario_has_placeholders_and_no_content()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.PlaceholdersOnly)
            .Scan(@"C:\demo");

        Assert.NotEmpty(report.Placeholders);
        Assert.Empty(report.IdenticalDuplicates);
        Assert.Empty(report.RealConflicts);

        var t = report.Telemetry;
        Assert.True(t.FilesPlaceholder > 0);
        Assert.Equal(0L, t.BytesRead);          // no content bytes read
        Assert.Equal(0L, t.PlaceholderBytesRead); // and no placeholder bytes
    }

    [Fact]
    public void Validator_flags_violation_when_enumerated_invariant_is_broken()
    {
        var broken = new ScanTelemetry
        {
            FilesEnumerated = 10,
            FilesPlaceholder = 2,
            FilesSkipped = 3,
            FilesPartialHashed = 4, // 2+3+4 = 9 ≠ 10
            FilesFullHashed = 4,
            BytesRead = 100,
            BytesReadPartial = 40,
            BytesReadFull = 60,
            PlaceholderBytesRead = 0,
        };

        var violations = broken.InvariantViolations();

        Assert.Contains(violations, v => v.Contains("files_enumerated"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Validator_flags_placeholder_bytes_read_not_equal_to_zero(long value)
    {
        var broken = new ScanTelemetry
        {
            FilesEnumerated = 1,
            FilesPlaceholder = 0,
            FilesSkipped = 0,
            FilesPartialHashed = 1,
            FilesFullHashed = 1,
            BytesRead = 100,
            BytesReadPartial = 40,
            BytesReadFull = 60,
            PlaceholderBytesRead = value,
        };

        var violations = broken.InvariantViolations();

        Assert.Contains(violations, v => v.Contains("placeholder_bytes_read"));
    }

    [Fact]
    public void Validator_flags_bytes_read_not_equal_to_partial_plus_full()
    {
        var broken = new ScanTelemetry
        {
            FilesEnumerated = 1,
            FilesPlaceholder = 0,
            FilesSkipped = 0,
            FilesPartialHashed = 1,
            FilesFullHashed = 1,
            BytesRead = 999, // ≠ 40 + 60
            BytesReadPartial = 40,
            BytesReadFull = 60,
            PlaceholderBytesRead = 0,
        };

        var violations = broken.InvariantViolations();

        Assert.Contains(violations, v => v.Contains("bytes_read"));
    }
}
