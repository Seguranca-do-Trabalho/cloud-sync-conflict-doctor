using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// T06 — esqueleto GUI: testes de ViewModel (não de UI), conforme o card.
/// Ciclo 1 (RED): o fluxo mínimo do §15, ponta a ponta, com motor falso.
/// </summary>
public class MainWindowViewModelTests
{
    [Fact]
    public void Fluxo_completo_escolher_pasta_ate_confirmacao()
    {
        var vm = new MainWindowViewModel(new FakeScanEngine());
        Assert.Equal(MainWindowViewModel.Screen.ChooseFolder, vm.CurrentScreen);

        vm.ChosenFolder = @"C:\Users\demo\OneDrive";
        vm.StartScanCommand.Execute(null);

        // Motor falso é síncrono: progresso completa e já abre o Resumo.
        Assert.Equal(100, vm.ScanProgressPercent);
        Assert.Equal(MainWindowViewModel.Screen.Summary, vm.CurrentScreen);
        Assert.NotNull(vm.Report);

        vm.OpenDuplicatesCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Duplicates, vm.CurrentScreen);

        vm.GoBackCommand.Execute(null); // Duplicatas → Resumo
        vm.OpenConflictsCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Conflicts, vm.CurrentScreen);

        var grupo = vm.Report!.RealConflicts[0];
        vm.CompareConflictCommand.Execute(grupo);
        Assert.Equal(MainWindowViewModel.Screen.Compare, vm.CurrentScreen);
        Assert.NotNull(vm.SelectedVersionToKeep); // sugestão determinística pré-escolhida

        vm.QueueOtherVersionsForQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.ChooseAction, vm.CurrentScreen);
        Assert.Equal(2, vm.QuarantineCount); // 3 versões − 1 mantida

        vm.ConfirmQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Quarantine, vm.CurrentScreen);

        vm.OpenConfirmationFromQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Confirmation, vm.CurrentScreen);

        vm.RestartCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.ChooseFolder, vm.CurrentScreen);
    }
}
