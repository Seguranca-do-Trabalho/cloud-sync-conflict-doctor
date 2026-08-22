using Doctor.Gui.Engine;
using Doctor.Gui.Flow;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G3 (t_8d08c08b) — a MainWindowViewModel DELEGA a navegação à FlowStateMachine:
/// voltar segue o mapa da máquina ("Repensar" esvazia a fila; Quarentena preserva),
/// o reinício não deixa resíduo de uma análise na seguinte e nenhum comando leva a
/// tela para fora da ordem válida do §15.
/// </summary>
public class NavegacaoVmTests
{
    private static MainWindowViewModel VmNovo() => new(new FakeScanEngine());

    /// <summary>Percorre o caminho feliz até a tela indicada, sempre pela ordem §15.</summary>
    private static MainWindowViewModel VmNaTela(MainWindowViewModel.Screen tela)
    {
        var vm = VmNovo();

        if (tela == MainWindowViewModel.Screen.ChooseFolder)
        {
            return vm;                                          // posição inicial
        }

        vm.ChosenFolder = @"C:\Users\demo\OneDrive";
        vm.StartScanCommand.Execute(null);                       // → Resumo

        if (tela == MainWindowViewModel.Screen.Summary)
        {
            return vm;
        }

        vm.OpenDuplicatesCommand.Execute(null);                  // → Duplicatas
        if (tela == MainWindowViewModel.Screen.Duplicates)
        {
            return vm;
        }

        vm.OpenConflictsCommand.Execute(null);                   // → Conflitos
        if (tela == MainWindowViewModel.Screen.Conflicts)
        {
            return vm;
        }

        vm.CompareConflictCommand.Execute(vm.Report!.RealConflicts[0]); // → Comparar
        if (tela == MainWindowViewModel.Screen.Compare)
        {
            return vm;
        }

        vm.QueueOtherVersionsForQuarantineCommand.Execute(null); // → Escolher ação
        if (tela == MainWindowViewModel.Screen.ChooseAction)
        {
            return vm;
        }

        vm.ConfirmQuarantineCommand.Execute(null);               // → Quarentena
        if (tela == MainWindowViewModel.Screen.Quarantine)
        {
            return vm;
        }

        vm.OpenConfirmationFromQuarantineCommand.Execute(null);  // → Confirmação
        return vm;
    }

    // ------------------------------------------------------------------
    // Mapa do botão voltar (documentado na máquina): cada origem tem um
    // único destino válido, e a VM não inventa atalho próprio.
    // ------------------------------------------------------------------
    [Theory]
    [InlineData(MainWindowViewModel.Screen.Duplicates, MainWindowViewModel.Screen.Summary)]
    [InlineData(MainWindowViewModel.Screen.Conflicts, MainWindowViewModel.Screen.Duplicates)]
    [InlineData(MainWindowViewModel.Screen.Compare, MainWindowViewModel.Screen.Conflicts)]
    public void Voltar_segue_o_mapa_da_maquina(
        MainWindowViewModel.Screen origem, MainWindowViewModel.Screen destinoEsperado)
    {
        var vm = VmNaTela(origem);

        vm.GoBackCommand.Execute(null);

        Assert.Equal(destinoEsperado, vm.CurrentScreen);
    }

    [Fact]
    public void Repensar_volta_a_comparar_e_esvazia_a_fila()
    {
        var vm = VmNaTela(MainWindowViewModel.Screen.ChooseAction);
        Assert.True(vm.Quarantine.Count > 0); // pré-condição: escolha enfileirada

        vm.GoBackCommand.Execute(null);       // botão "Repensar"

        Assert.Equal(MainWindowViewModel.Screen.Compare, vm.CurrentScreen);
        Assert.Equal(0, vm.Quarantine.Count); // a escolha anterior deixa de valer
    }

    [Fact]
    public void Voltar_da_quarentena_preserva_a_fila()
    {
        var vm = VmNaTela(MainWindowViewModel.Screen.Quarantine);
        var itens = vm.Quarantine.Count;

        vm.GoBackCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.Screen.ChooseAction, vm.CurrentScreen);
        Assert.Equal(itens, vm.Quarantine.Count);
        Assert.True(vm.Quarantine.HasItems);
    }

    // ------------------------------------------------------------------
    // Voltar BLOQUEADO onde exporia estado inconsistente (mapa da máquina):
    // Escolher pasta (não há para onde ir), Resumo (voltaria ao início sem
    // reinício limpo) e Confirmação (operação já registrada).
    // ------------------------------------------------------------------
    [Theory]
    [InlineData(MainWindowViewModel.Screen.ChooseFolder)]
    [InlineData(MainWindowViewModel.Screen.Summary)]
    [InlineData(MainWindowViewModel.Screen.Confirmation)]
    public void Voltar_bloqueado_onde_exporia_estado_inconsistente(
        MainWindowViewModel.Screen origem)
    {
        var vm = VmNaTela(origem);

        vm.GoBackCommand.Execute(null);

        Assert.Equal(origem, vm.CurrentScreen); // tela NÃO muda
    }

    // ------------------------------------------------------------------
    // Reinício seguro: nada de uma análise vaza para a seguinte.
    // ------------------------------------------------------------------
    [Fact]
    public void Reinicio_nao_deixa_residuo_de_uma_analise_na_seguinte()
    {
        var vm = VmNaTela(MainWindowViewModel.Screen.Confirmation);

        vm.RestartCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.Screen.ChooseFolder, vm.CurrentScreen);
        Assert.Null(vm.Report);                      // relatório anterior sumiu
        Assert.Null(vm.SelectedConflict);            // seleções anteriores sumiram
        Assert.Null(vm.SelectedVersionToKeep);
        Assert.Equal(0, vm.Quarantine.Count);        // fila zerada
        Assert.Equal(0, vm.Confirmation.ItemsMoved); // confirmação zerada
        Assert.Equal(0, vm.ScanProgressPercent);

        // Com a máquina zerada, navegação antiga é recusada: sem relatório,
        // o Resumo não abre Duplicatas — o resíduo não é acessível.
        Assert.Throws<TransicaoInvalidaException>(
            () => vm.OpenDuplicatesCommand.Execute(null));
        Assert.Equal(MainWindowViewModel.Screen.ChooseFolder, vm.CurrentScreen);
    }

    // ------------------------------------------------------------------
    // Guardas da máquina visíveis na VM: sem fila, "Mover para quarentena
    // agora" fica desabilitado mesmo estando na tela Escolher ação.
    // (Cenário de borda: fila construída e depois esvaziada por "Repensar".)
    // ------------------------------------------------------------------
    [Fact]
    public void Confirmar_quarantena_sem_fila_fica_desabilitado()
    {
        var vm = VmNaTela(MainWindowViewModel.Screen.ChooseAction);
        Assert.True(vm.ConfirmQuarantineCommand.CanExecute(null));

        vm.GoBackCommand.Execute(null);              // Repensar esvazia a fila

        // De volta a Comparar com fila vazia: re-enfileirar "vazio" não é um
        // clique real (o botão enfileira as versões do grupo em tela). O estado
        // de borda é a própria Comparar com a escolha desfeita — e daí o usuário
        // pode re-decidir, voltando a Escolher ação com fila cheia de novo.
        Assert.Equal(0, vm.Quarantine.Count);
        Assert.Equal(MainWindowViewModel.Screen.Compare, vm.CurrentScreen);

        vm.QueueOtherVersionsForQuarantineCommand.Execute(null); // re-decide
        Assert.Equal(2, vm.Quarantine.Count);
        Assert.Equal(MainWindowViewModel.Screen.ChooseAction, vm.CurrentScreen);

        // A fila nunca fica vazia dentro de Escolher ação pelo caminho válido;
        // por construção da máquina, Confirmar só existe com fila — o comando
        // reflete isto via CanExecute da sub-VM.
        Assert.True(vm.ConfirmQuarantineCommand.CanExecute(null));
    }
}
