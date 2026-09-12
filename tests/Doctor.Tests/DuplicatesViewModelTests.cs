using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// t_2a116a88 — cycle 3 (RED): the Duplicates screen has its own ViewModel,
/// consuming the report in schema v1 shape. The MainWindow no longer exposes
/// duplicate data: it exposes only the sub-VM.
/// </summary>
public class DuplicatesViewModelTests
{
    private static ScanReport ReportNominal() =>
        new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal).Scan(@"C:\demo");

    [Fact]
    public void Groups_list_with_redundant_copy_count_and_formatted_bytes()
    {
        var duplicates = new DuplicatesViewModel { Report = ReportNominal() };

        Assert.Equal(2, duplicates.Groups.Count);

        var docx = duplicates.Groups[0];
        Assert.Equal("Relatorios/2026/copia-relatorio-anual.docx", docx.KeptPath);
        Assert.Equal(3, duplicates.RedundantCopies); // (3-1) + (2-1) = 3 redundant copies

        // Bytes per class with fixed pt-BR separator, never locale.
        Assert.Equal("1 MB", docx.SizeLabel);
    }

    [Fact]
    public void Without_report_no_groups_and_count_is_zero()
    {
        var duplicates = new DuplicatesViewModel();

        Assert.Empty(duplicates.Groups);
        Assert.Equal("0", duplicates.RedundantCopiesLabel);
    }

    [Fact]
    public void No_duplicates_scenario_list_empty_and_count_zero()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.NoDuplicates).Scan(@"C:\demo");
        var duplicates = new DuplicatesViewModel { Report = report };

        Assert.Empty(duplicates.Groups);
        Assert.Equal("0", duplicates.RedundantCopiesLabel);
    }
}
