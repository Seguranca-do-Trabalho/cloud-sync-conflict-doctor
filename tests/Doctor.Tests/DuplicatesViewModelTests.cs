using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// t_2a116a88 — ciclo 3 (RED): a tela Duplicatas tem ViewModel próprio,
/// consumindo o relatório em forma schema v1. A MainWindow deixa de expor
/// dados de duplicatas: expõe somente a sub-VM.
/// </summary>
public class DuplicatesViewModelTests
{
    private static ScanReport ReportNominal() =>
        new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal).Scan(@"C:\demo");

    [Fact]
    public void Lista_grupos_com_contagem_de_copias_redundantes_e_bytes_formatados()
    {
        var duplicates = new DuplicatesViewModel { Report = ReportNominal() };

        Assert.Equal(2, duplicates.Groups.Count);

        var docx = duplicates.Groups[0];
        Assert.Equal("Relatorios/2026/copia-relatorio-anual.docx", docx.KeptPath);
        Assert.Equal(3, duplicates.RedundantCopies); // (3-1) + (2-1) = 3 cópias redundantes

        // Bytes por classe com separador pt-BR fixo, nunca locale.
        Assert.Equal("1 MB", docx.SizeLabel);
    }

    [Fact]
    public void Sem_relatorio_nao_tem_grupos_e_contagem_e_zero()
    {
        var duplicates = new DuplicatesViewModel();

        Assert.Empty(duplicates.Groups);
        Assert.Equal("0", duplicates.RedundantCopiesLabel);
    }

    [Fact]
    public void Cenario_sem_duplicatas_lista_vazia_e_contagem_zero()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.SemDuplicatas).Scan(@"C:\demo");
        var duplicates = new DuplicatesViewModel { Report = report };

        Assert.Empty(duplicates.Groups);
        Assert.Equal("0", duplicates.RedundantCopiesLabel);
    }
}
