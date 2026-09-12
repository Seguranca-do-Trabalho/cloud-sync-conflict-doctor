using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// T06 — GUI skeleton: ViewModel tests (not UI), per the card.
/// Cycle 1 (RED): the minimum flow of §15, end-to-end, with fake engine.
/// </summary>
public class MainWindowViewModelTests
{
    [Fact]
    public void Complete_flow_choose_folder_through_confirmation()
    {
        var vm = new MainWindowViewModel(new FakeScanEngine());
        Assert.Equal(MainWindowViewModel.Screen.ChooseFolder, vm.CurrentScreen);

        vm.ChosenFolder = @"C:\Users\demo\OneDrive";
        vm.StartScanCommand.Execute(null);

        // Fake engine is synchronous: progress completes and already opens Summary.
        Assert.Equal(100, vm.ScanProgressPercent);
        Assert.Equal(MainWindowViewModel.Screen.Summary, vm.CurrentScreen);
        Assert.NotNull(vm.Report);

        vm.OpenDuplicatesCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Duplicates, vm.CurrentScreen);

        vm.GoBackCommand.Execute(null); // Duplicates → Summary
        vm.OpenConflictsCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Conflicts, vm.CurrentScreen);

        var group = vm.Report!.RealConflicts[0];
        vm.CompareConflictCommand.Execute(group);
        Assert.Equal(MainWindowViewModel.Screen.Compare, vm.CurrentScreen);
        Assert.NotNull(vm.SelectedVersionToKeep); // deterministic pre-chosen suggestion

        vm.QueueOtherVersionsForQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.ChooseAction, vm.CurrentScreen);
        Assert.Equal(2, vm.Quarantine.Count); // 3 versions − 1 kept

        vm.ConfirmQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Quarantine, vm.CurrentScreen);

        vm.OpenConfirmationFromQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Confirmation, vm.CurrentScreen);

        vm.RestartCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.ChooseFolder, vm.CurrentScreen);
    }
}
