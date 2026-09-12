using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// t_2a116a88 — cycle 4 (RED): the Real Conflicts screen has its own ViewModel,
/// consuming the report in schema v1 shape, with the deterministic
/// mtime → size → path suggestion exposed per row.
/// </summary>
public class ConflictsViewModelTests
{
    private static ScanReport ReportNominal() =>
        new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal).Scan(@"C:\demo");

    [Fact]
    public void Groups_list_with_suggested_kept_version_and_unkept_bytes()
    {
        var conflicts = new ConflictsViewModel { Report = ReportNominal() };

        Assert.Single(conflicts.Groups);
        var group = conflicts.Groups[0];

        // Deterministic suggestion: newest mtime → DESKTOP-4K2F version.
        Assert.Equal(
            "Projetos/orcamento (DESKTOP-4K2F conflicted copy 2026-08-13).xlsx",
            group.SuggestedKeepPath);

        // Quarantine-eligible bytes in this group: sum of versions − kept.
        Assert.Equal(88_412L + 91_077L, group.BytesToQuarantine);

        // Common group size (schema §6.2) formatted: 91,077/1,024 = 88.94 KB.
        Assert.Equal("88.94 KB", group.TotalSizeLabel);
    }

    [Fact]
    public void Without_report_list_is_empty()
    {
        var conflicts = new ConflictsViewModel();

        Assert.Empty(conflicts.Groups);
    }

    [Fact]
    public void No_conflicts_scenario_list_is_empty()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.NoConflicts).Scan(@"C:\demo");
        var conflicts = new ConflictsViewModel { Report = report };

        Assert.Empty(conflicts.Groups);
    }

    [Fact]
    public void Mtime_and_size_tie_prefers_shorter_path_in_utf8_bytes()
    {
        var report = ReportNominal();
        var tie = new ConflictGroup
        {
            BaseName = "test.txt",
            TotalBytes = 10,
            Versions =
            [
                new ConflictVersion
                {
                    Path = "b/test.txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
                new ConflictVersion
                {
                    Path = "a/test.txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            ],
        };

        var row = new ConflictGroupRow(tie);

        // Complete mtime and size tie: shortest path in UTF-8 bytes ("a/…").
        Assert.Equal("a/test.txt", row.SuggestedKeepPath);
    }
}
