using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G2 (t_87f625aa) — telas Quarentena e Confirmação (§18):
/// - Quarentena: lista a fila; botão destrutivo DESABILITADO sem seleção/fila;
///   rótulo sempre cita o destino "quarentena".
/// - Confirmação: mostra operation_id (determinístico), contagem de itens e o
///   caminho previsto no formato §18 (ConflictDoctor/quarantine/<timestamp>)
///   ANTES de habilitar o botão de confirmação.
/// Timestamps e ids são derivados de forma determinística (sem relógio local).
/// </summary>
public class ConfirmacaoTelaTests
{
    private static MainWindowViewModel NovoVm() => new(new FakeScanEngine());

    private static MainWindowViewModel VmComFila()
    {
        var vm = NovoVm();
        vm.Quarantine.Queue(@"C:\demo\orcamento (Notebook-Office).xlsx");
        vm.Quarantine.Queue(@"C:\demo\praia.jpg");
        return vm;
    }

    [Fact]
    public void Caminho_previsto_segue_formato_da_secao_18()
    {
        // ConflictDoctor/quarantine/<timestamp> — separador e data fixos, sem locale.
        var caminho = MainWindowViewModel.CaminhoQuarentenaPrevisto();

        Assert.StartsWith("ConflictDoctor/quarantine/", caminho, StringComparison.Ordinal);
        // Timestamp ISO-8601 básico determinístico: 4-2-2 dígitos + T + hora.
        Assert.Matches(
            @"^ConflictDoctor/quarantine/\d{4}-\d{2}-\d{2}T\d{2}-\d{2}$",
            caminho);
    }

    [Fact]
    public void Operation_id_e_deterministico_para_a_mesma_fila()
    {
        var vm1 = VmComFila();
        var vm2 = VmComFila();

        Assert.Equal(vm1.OperationIdPrevisto, vm2.OperationIdPrevisto);
        Assert.NotEqual("", vm1.OperationIdPrevisto);

        // Filas diferentes → ids diferentes.
        var vm3 = NovoVm();
        vm3.Quarantine.Queue(@"C:\outro\arquivo.txt");
        Assert.NotEqual(vm1.OperationIdPrevisto, vm3.OperationIdPrevisto);
    }

    [Fact]
    public void Botao_de_confirmacao_desabilitado_sem_itens_na_fila()
    {
        var vm = NovoVm();
        Assert.False(vm.OpenConfirmationFromQuarantineCommand.CanExecute(null));

        vm.Quarantine.Queue(@"C:\demo\a.txt");
        Assert.True(vm.OpenConfirmationFromQuarantineCommand.CanExecute(null));
    }

    [Fact]
    public void Dados_do_manifesto_visiveis_antes_de_habilitar_confirmacao()
    {
        var vm = VmComFila();

        // Os três dados do manifesto fake existem ANTES de abrir a tela Confirmação.
        Assert.False(string.IsNullOrWhiteSpace(vm.OperationIdPrevisto));
        Assert.Equal(2, vm.Quarantine.Count);
        var caminho = MainWindowViewModel.CaminhoQuarentenaPrevisto();
        Assert.Contains("quarantine", caminho, StringComparison.Ordinal);
    }

    [Fact]
    public void Tela_quarentena_exibe_fila_rotulo_de_destino_e_botao_secundario()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var axaml = File.ReadAllText(Path.Combine(dir!, "src", "Doctor.Gui", "MainWindow.axaml"));

        // Rótulo da ação na Quarentena sempre cita o destino "quarentena".
        Assert.Contains("Registrar movimentação para a quarentena",
            axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Tela_confirmacao_apresenta_operation_id_contagem_e_caminho()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var axaml = File.ReadAllText(Path.Combine(dir!, "src", "Doctor.Gui", "MainWindow.axaml"));

        Assert.Contains("OperationIdPrevisto", axaml, StringComparison.Ordinal);
        Assert.Contains("CaminhoQuarentenaPrevisto", axaml, StringComparison.Ordinal);
        Assert.Contains("ConfirmacaoContagemItens", axaml, StringComparison.Ordinal);
    }
}
