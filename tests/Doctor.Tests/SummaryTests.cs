using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// T06 — cycle 2 (RED): the first post-scan screen (Summary) answers the 5 questions of §15.
/// Expected values come from FakeScanEngine DEMO data.
/// </summary>
public class SummaryTests
{
    private static MainWindowViewModel NewVmAfterScan()
    {
        var vm = new MainWindowViewModel(new Doctor.Gui.Engine.FakeScanEngine());
        vm.ChosenFolder = @"C:\Users\demo\OneDrive";
        vm.StartScanCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void Summary_answers_the_five_spec_questions()
    {
        var vm = NewVmAfterScan();

        // 1. How many files were found? (invariant integer, no thousand separator)
        // Adapted to schema v1 (t_2a116a88): files_enumerated from DEMO dataset
        // (2 placeholders + 4 uniques + 10 partially hashed = 16).
        Assert.Equal("16", vm.SummaryFilesFound);

        // 2. How many identical duplicates? (redundant copies: (3-1) + (2-1))
        Assert.Equal("3", vm.SummaryIdenticalDuplicates);

        // 3. How many real conflicts?
        Assert.Equal("1", vm.SummaryRealConflicts);

        // 4. How many placeholders were ignored?
        Assert.Equal("2", vm.SummaryPlaceholdersIgnored);

        // 5. How much space can be safely recovered?
        // Formula from card t_2a116a88: losers from identical duplicates
        // (2×1MiB + 2,458,912 B = 4,556,064) + versions from conflicts that will not be
        // kept (88,412 + 91,077 = 179,489) = 4,735,553 B / 1,048,576 = 4.52 MiB.
        Assert.Equal("4.52 MB", vm.SummaryRecoverableSpace);
    }
}

/// <summary>Byte formatting: locale-invariant (determinism §3), consumer-readable.</summary>
public class FormatBytesTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(2048, "2 KB")]
    [InlineData(1_048_576, "1 MB")]
    [InlineData(4_556_064, "4.35 MB")] // 2*1MiB + 2458912 → exactly the DEMO summary
    [InlineData(1_610_612_736, "1.5 GB")]
    public void Formats_bytes_in_readable_units(long bytes, string expected) =>
        Assert.Equal(expected, MainWindowViewModel.FormatBytes(bytes));
}
