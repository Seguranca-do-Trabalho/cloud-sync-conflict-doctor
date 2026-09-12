using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// T06 — cycle 3 (RED): quarantine queue safety and suggestion determinism.
/// ADR-0002: only operation allowed on user content is moving to quarantine;
/// never "delete". ADR-0003: tie resolved mtime → size → path, path in UTF-8 bytes.
/// </summary>
public class SecurityTests
{
    private static MainWindowViewModel NewVm() => new(new FakeScanEngine());

    [Fact]
    public void Quarantine_queue_does_not_duplicate_repeated_item()
    {
        var vm = NewVm();
        vm.Quarantine.Queue("C:/demo/a.txt");
        vm.Quarantine.Queue("C:/demo/a.txt");

        Assert.Equal(["C:/demo/a.txt"], vm.Quarantine.Paths.ToArray());
    }

    [Fact]
    public void Version_to_keep_suggestion_uses_mtime_then_size_then_path_in_bytes()
    {
        // mtime wins; on tie, larger size wins; on tie, smaller path in UTF-8 bytes.
        var group = new ConflictGroup
        {
            BaseName = "doc.txt",
            Versions =
            [
                new ConflictVersion { Path = "doc (b).txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new ConflictVersion { Path = "doc (a).txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new ConflictVersion { Path = "doc (large).txt", SizeBytes = 999,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new ConflictVersion { Path = "doc (new).txt", SizeBytes = 5,
                    MtimeUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero) },
            ],
        };

        var kept = MainWindowViewModel.SuggestVersionToKeep(group);

        Assert.Equal("doc (new).txt", kept!.Path); // most recent mtime
    }

    [Fact]
    public void Total_mtime_and_size_tie_prefers_smaller_path_in_utf8_bytes()
    {
        var group = new ConflictGroup
        {
            BaseName = "doc.txt",
            Versions =
            [
                new ConflictVersion { Path = "doc (B).txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new ConflictVersion { Path = "doc (A).txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            ],
        };

        var kept = MainWindowViewModel.SuggestVersionToKeep(group);

        Assert.Equal("doc (A).txt", kept!.Path);
    }

    [Fact]
    public void Quarantine_confirmation_requires_non_empty_queue()
    {
        var vm = NewVm();
        Assert.False(vm.ConfirmQuarantineCommand.CanExecute(null)); // empty: disabled

        vm.Quarantine.Queue("C:/demo/a.txt");
        Assert.True(vm.ConfirmQuarantineCommand.CanExecute(null));
    }

    [Fact]
    public void Compare_enqueues_only_versions_that_are_not_kept()
    {
        var vm = NewVm();
        vm.ChosenFolder = @"C:\demo";
        vm.StartScanCommand.Execute(null);
        vm.OpenDuplicatesCommand.Execute(null);   // §15: Summary → Duplicates
        vm.OpenConflictsCommand.Execute(null);    // §15: Duplicates → Conflicts

        var group = vm.Report!.RealConflicts[0];
        vm.CompareConflictCommand.Execute(group);
        vm.QueueOtherVersionsForQuarantineCommand.Execute(null);

        // Kept = version with most recent mtime (08/13); the other 2 enter the queue.
        Assert.Equal(2, vm.Quarantine.Count);
        Assert.DoesNotContain(vm.SelectedVersionToKeep!.Path, vm.Quarantine.Paths);
        Assert.All(vm.Quarantine.Paths, p => Assert.NotEqual(vm.SelectedVersionToKeep!.Path, p));

        // Repeating the operation does not duplicate items in queue: the user goes back
        // from "Rethink" (valid path §15 — the queued choice no longer applies,
        // back to Compare with the same group), re-decides and enqueues again.
        vm.GoBackCommand.Execute(null);
        vm.QueueOtherVersionsForQuarantineCommand.Execute(null);
        Assert.Equal(2, vm.Quarantine.Count);
    }
}
