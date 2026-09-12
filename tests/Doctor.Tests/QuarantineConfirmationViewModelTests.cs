using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// t_2a116a88 — cycle 5 (RED): quarantine queue and confirmation moved from
/// MainWindowViewModel to dedicated sub-VMs. MainWindow retains navigation
/// (Screen enum) and orchestration only.
/// </summary>
public class QuarantineConfirmationViewModelTests
{
    private static ScanReport ReportNominal() =>
        new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal).Scan(@"C:\demo");

    [Fact]
    public void Queue_accepts_item_without_duplicates_and_counts_in_raw_digits()
    {
        var quarantine = new QuarantineViewModel();

        Assert.False(quarantine.HasItems);
        quarantine.Queue("Photos/trip-2025/beach - copy.jpg");
        quarantine.Queue("Photos/trip-2025/beach - copy.jpg"); // duplicate: does not add twice

        Assert.Equal(1, quarantine.Count);
        Assert.True(quarantine.HasItems);
        Assert.Single(quarantine.Paths);
        Assert.Equal("1", quarantine.CountLabel);
    }

    [Fact]
    public void Clear_empties_queue()
    {
        var quarantine = new QuarantineViewModel();
        quarantine.Queue("Projects/budget.xlsx");

        quarantine.Clear();

        Assert.Equal(0, quarantine.Count);
        Assert.False(quarantine.HasItems);
        Assert.Equal("0", quarantine.CountLabel);
    }

    [Fact]
    public void Confirmation_summarizes_queue_with_safety_message()
    {
        var confirmation = new ConfirmationViewModel();
        confirmation.Complete(["a.txt", "b.txt", "c.txt"]);

        Assert.Equal(3, confirmation.ItemsMoved);
        Assert.Equal("3", confirmation.ItemsMovedLabel);

        // Safety §2: nothing is discarded; label never says delete.
        Assert.Contains("quarantine", confirmation.SafetyMessage);
        Assert.DoesNotContain("delet", confirmation.SafetyMessage.ToLowerInvariant());
    }

    [Fact]
    public void Confirmation_without_queue_has_safe_zero_state()
    {
        var confirmation = new ConfirmationViewModel();

        Assert.Equal(0, confirmation.ItemsMoved);
        Assert.Equal("0", confirmation.ItemsMovedLabel);
        Assert.NotEmpty(confirmation.SafetyMessage);
    }

    [Fact]
    public void Orchestration_MainWindow_delegates_to_sub_vms()
    {
        var vm = new MainWindowViewModel(new FakeScanEngine());
        vm.ChosenFolder = @"C:\Users\demo\OneDrive";
        vm.StartScanCommand.Execute(null);
        vm.OpenDuplicatesCommand.Execute(null);   // §15: Summary → Duplicates
        vm.OpenConflictsCommand.Execute(null);    // §15: Duplicates → Conflicts

        // Comparison queues unkept versions in quarantine sub-VM.
        vm.CompareConflictCommand.Execute(vm.Report!.RealConflicts[0]);
        vm.QueueOtherVersionsForQuarantineCommand.Execute(null);

        Assert.Equal(2, vm.Quarantine.Count); // 3 versions - 1 kept

        // Confirming moves queue to confirmation sub-VM.
        vm.ConfirmQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Quarantine, vm.CurrentScreen);

        vm.OpenConfirmationFromQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Confirmation, vm.CurrentScreen);
        Assert.Equal(2, vm.Confirmation.ItemsMoved);

        // Restarting clears queue and confirmation (fresh scan starts from zero).
        vm.RestartCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.ChooseFolder, vm.CurrentScreen);
        Assert.Equal(0, vm.Quarantine.Count);
        Assert.Equal(0, vm.Confirmation.ItemsMoved);
    }
}
