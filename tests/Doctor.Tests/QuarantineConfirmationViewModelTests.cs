using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// t_2a116a88 — ciclo 5 (RED): fila de quarentena e confirmação saem da
/// MainWindowViewModel para sub-VMs próprias. A MainWindow fica com
/// navegação (Screen enum) e orquestração apenas.
/// </summary>
public class QuarantineConfirmationViewModelTests
{
    private static ScanReport ReportNominal() =>
        new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal).Scan(@"C:\demo");

    [Fact]
    public void Fila_aceita_item_sem_duplicar_e_conta_em_digitos_crus()
    {
        var quarantine = new QuarantineViewModel();

        Assert.False(quarantine.HasItems);
        quarantine.Queue("Fotos/viagem-2025/praia - copia.jpg");
        quarantine.Queue("Fotos/viagem-2025/praia - copia.jpg"); // repetido: não duplica

        Assert.Equal(1, quarantine.Count);
        Assert.True(quarantine.HasItems);
        Assert.Single(quarantine.Paths);
        Assert.Equal("1", quarantine.CountLabel);
    }

    [Fact]
    public void Limpar_esvazia_a_fila()
    {
        var quarantine = new QuarantineViewModel();
        quarantine.Queue("Projetos/orcamento.xlsx");

        quarantine.Clear();

        Assert.Equal(0, quarantine.Count);
        Assert.False(quarantine.HasItems);
        Assert.Equal("0", quarantine.CountLabel);
    }

    [Fact]
    public void Confirmacao_resume_fila_com_mensagem_de_seguranca()
    {
        var confirmation = new ConfirmationViewModel();
        confirmation.Complete(["a.txt", "b.txt", "c.txt"]);

        Assert.Equal(3, confirmation.ItemsMoved);
        Assert.Equal("3", confirmation.ItemsMovedLabel);

        // Segurança §2: nada é descartado; rótulo nunca fala em apagar.
        Assert.Contains("quarentena", confirmation.SafetyMessage);
        Assert.DoesNotContain("apag", confirmation.SafetyMessage.ToLowerInvariant());
        Assert.DoesNotContain("delet", confirmation.SafetyMessage.ToLowerInvariant());
    }

    [Fact]
    public void Confirmacao_sem_fila_tem_estado_zero_seguro()
    {
        var confirmation = new ConfirmationViewModel();

        Assert.Equal(0, confirmation.ItemsMoved);
        Assert.Equal("0", confirmation.ItemsMovedLabel);
        Assert.NotEmpty(confirmation.SafetyMessage);
    }

    [Fact]
    public void Orquestracao_MainWindow_delega_para_sub_vms()
    {
        var vm = new MainWindowViewModel(new FakeScanEngine());
        vm.ChosenFolder = @"C:\Users\demo\OneDrive";
        vm.StartScanCommand.Execute(null);

        // Comparação enfileira as não mantidas na sub-VM de quarentena.
        vm.CompareConflictCommand.Execute(vm.Report!.RealConflicts[0]);
        vm.QueueOtherVersionsForQuarantineCommand.Execute(null);

        Assert.Equal(2, vm.Quarantine.Count); // 3 versões − 1 mantida

        // Confirmar move a fila para a sub-VM de confirmação.
        vm.ConfirmQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Quarantine, vm.CurrentScreen);

        vm.OpenConfirmationFromQuarantineCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.Confirmation, vm.CurrentScreen);
        Assert.Equal(2, vm.Confirmation.ItemsMoved);

        // Reiniciar limpa fila e confirmação (novo scan começa do zero).
        vm.RestartCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.Screen.ChooseFolder, vm.CurrentScreen);
        Assert.Equal(0, vm.Quarantine.Count);
        Assert.Equal(0, vm.Confirmation.ItemsMoved);
    }
}
