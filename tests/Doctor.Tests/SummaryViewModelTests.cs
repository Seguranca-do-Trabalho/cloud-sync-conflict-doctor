using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// t_2a116a88 — cycle 2 (RED): the Summary is now a dedicated SummaryViewModel
/// (GUIVM-01), answering the 5 §15 questions FROM report data in schema v1
/// shape — never from loose numbers.
/// </summary>
public class SummaryViewModelTests
{
    private static ScanReport ReportNominal() =>
        new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal).Scan(@"C:\demo");

    [Fact]
    public void SummaryViewModel_AnswersFiveQuestions_FromReportData()
    {
        var summary = new SummaryViewModel { Report = ReportNominal() };

        // 1. Files found = files_enumerated (raw digits, InvariantCulture).
        Assert.Equal("16", summary.FilesFound);

        // 2. Identical duplicates = redundant copies: (3-1) + (2-1).
        Assert.Equal("3", summary.IdenticalDuplicates);

        // 3. Real conflicts = number of groups with real divergence.
        Assert.Equal("1", summary.RealConflicts);

        // 4. Placeholders ignored = files_placeholder (never opened).
        Assert.Equal("2", summary.PlaceholdersIgnored);

        // 5. Recoverable space = explicitly tested formula below.
        // Duplicate losers (4,556,064) + unkept conflict versions
        // (179,489) = 4,735,553 B → 4.52 MB with invariant separator.
        Assert.Equal("4.52 MB", summary.RecoverableSpace);
    }

    /// <summary>
    /// The recoverable space formula is explicitly tested, by part:
    /// identical duplicate losers + versions that will not be kept
    /// in real conflicts (deterministic suggestion mtime → size → path).
    /// </summary>
    [Fact]
    public void Recoverable_space_formula_sums_losers_and_unkept_versions()
    {
        var report = ReportNominal();

        // Part 1 — identical duplicates: all copies minus the kept one
        // (kept = shortest path in UTF-8 bytes within the class).
        long duplicateLosers =
            report.IdenticalDuplicates.Sum(g => (long)(g.Files.Count - 1) * g.SizeBytes);
        Assert.Equal(2 * 1_048_576L + 1 * 2_458_912L, duplicateLosers); // 4,556,064

        // Part 2 — real conflicts: sum of versions that will NOT be kept
        // (suggested kept = newest mtime → "orcamento (DESKTOP-4K2F…)", 90,240 B).
        long unkeptConflicts = report.RealConflicts.Sum(g =>
            g.Versions.Where(v => !ReferenceEquals(v, ScanReport.SuggestVersionToKeep(g)))
                      .Sum(v => v.SizeBytes));
        Assert.Equal(88_412L + 91_077L, unkeptConflicts); // 179,489

        // Expected total and report property match the formula.
        long expected = duplicateLosers + unkeptConflicts;
        Assert.Equal(expected, report.RecoverableBytes);
        Assert.Equal(4_556_064L + 179_489L, report.RecoverableBytes); // 4,735,553
    }

    [Fact]
    public void Without_report_all_five_answers_are_safely_zero()
    {
        var summary = new SummaryViewModel();

        Assert.Equal("0", summary.FilesFound);
        Assert.Equal("0", summary.IdenticalDuplicates);
        Assert.Equal("0", summary.RealConflicts);
        Assert.Equal("0", summary.PlaceholdersIgnored);
        Assert.Equal("0 B", summary.RecoverableSpace);
    }

    [Theory]
    [InlineData(FakeScanEngine.ScanScenario.NoDuplicates)]
    [InlineData(FakeScanEngine.ScanScenario.NoConflicts)]
    [InlineData(FakeScanEngine.ScanScenario.PlaceholdersOnly)]
    public void Engine_edge_cases_produce_coherent_summary(FakeScanEngine.ScanScenario scenario)
    {
        var report = new FakeScanEngine(scenario).Scan(@"C:\demo");
        var summary = new SummaryViewModel { Report = report };

        if (scenario == FakeScanEngine.ScanScenario.NoDuplicates)
        {
            Assert.Equal("0", summary.IdenticalDuplicates);
            Assert.Equal("1", summary.RealConflicts);
        }

        if (scenario == FakeScanEngine.ScanScenario.NoConflicts)
        {
            Assert.Equal("3", summary.IdenticalDuplicates);
            Assert.Equal("0", summary.RealConflicts);
        }

        if (scenario == FakeScanEngine.ScanScenario.PlaceholdersOnly)
        {
            // Placeholders only: nothing eligible for quarantine, nothing recoverable.
            Assert.Equal("2", summary.PlaceholdersIgnored);
            Assert.Equal("0", summary.IdenticalDuplicates);
            Assert.Equal("0", summary.RealConflicts);
            Assert.Equal("0 B", summary.RecoverableSpace);
        }
    }
}
