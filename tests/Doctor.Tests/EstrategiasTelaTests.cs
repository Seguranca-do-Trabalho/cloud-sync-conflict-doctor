using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G2 (t_87f625aa) — tela "Escolher ação": apresentação FIXA das 4 estratégias do §17
/// ('manter mais recente', 'manter maior', 'manter versão de determinada máquina',
/// 'escolher manualmente') com a regra de empate visível (mtime → size → path, §17).
/// APENAS apresentação visual: a lógica de resolução é do EPIC 07 (scheduled).
/// Ponto de extensão documentado — nada aqui decide qual versão manter além da
/// sugestão determinística já existente (mtime→size→path).
/// </summary>
public class EstrategiasTelaTests
{
    private static MainWindowViewModel NovoVm() => new(new FakeScanEngine());

    [Fact]
    public void Lista_de_estrategias_e_fixa_com_quatro_opcoes_na_ordem_da_spec()
    {
        var estrategias = MainWindowViewModel.EstrategiasResolucao;

        Assert.Equal(4, estrategias.Count);
        Assert.Equal(
        [
            "Manter mais recente",
            "Manter maior",
            "Manter versão de determinada máquina",
            "Escolher manualmente",
        ], estrategias);
    }

    [Fact]
    public void Regra_de_empate_visivel_e_mtime_size_path()
    {
        Assert.Equal("mtime → size → path", MainWindowViewModel.RegraDeEmpate);
    }

    [Fact]
    public void Estrategias_sao_apresentadas_na_tela_escolher_acao()
    {
        // A tela Escolher ação apresenta a lista CANÔNICA do ViewModel (fonte única,
        // sem duplicar literais) e exibe a regra de empate do §17.
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var caminho = Path.Combine(dir!, "src", "Doctor.Gui", "MainWindow.axaml");
        var axaml = File.ReadAllText(caminho);

        Assert.Contains("ItemsSource=\"{x:Static vm:MainWindowViewModel.EstrategiasResolucao}\"",
            axaml, StringComparison.Ordinal);
        Assert.Contains(MainWindowViewModel.RegraDeEmpate, axaml, StringComparison.Ordinal);
    }
}
