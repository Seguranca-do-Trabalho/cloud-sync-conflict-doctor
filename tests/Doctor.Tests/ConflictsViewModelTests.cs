using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// t_2a116a88 — ciclo 4 (RED): a tela Conflitos reais tem ViewModel próprio,
/// consumindo o relatório em forma schema v1, com a sugestão determinística
/// mtime → size → path exposta por linha.
/// </summary>
public class ConflictsViewModelTests
{
    private static ScanReport ReportNominal() =>
        new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal).Scan(@"C:\demo");

    [Fact]
    public void Lista_grupos_com_versao_mantida_sugerida_e_bytes_nao_mantidos()
    {
        var conflicts = new ConflictsViewModel { Report = ReportNominal() };

        Assert.Single(conflicts.Groups);
        var grupo = conflicts.Groups[0];

        // Sugestão determinística: mtime mais recente → versão DESKTOP-4K2F.
        Assert.Equal(
            "Projetos/orcamento (DESKTOP-4K2F conflicted copy 2026-08-13).xlsx",
            grupo.SuggestedKeepPath);

        // Bytes elegíveis para quarentena neste grupo: soma das versões − mantida.
        Assert.Equal(88_412L + 91_077L, grupo.BytesToQuarantine);

        // Tamanho comum do grupo (schema §6.2) formatado pt-BR fixo: 91.077/1.024 = 88,94 KB.
        Assert.Equal("88,94 KB", grupo.TotalSizeLabel);
    }

    [Fact]
    public void Sem_relatorio_lista_vazia()
    {
        var conflicts = new ConflictsViewModel();

        Assert.Empty(conflicts.Groups);
    }

    [Fact]
    public void Cenario_sem_conflitos_lista_vazia()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.SemConflitos).Scan(@"C:\demo");
        var conflicts = new ConflictsViewModel { Report = report };

        Assert.Empty(conflicts.Groups);
    }

    [Fact]
    public void Empate_de_mtime_e_size_prefere_menor_caminho_em_bytes_utf8()
    {
        var report = ReportNominal();
        var empate = new ConflictGroup
        {
            BaseName = "teste.txt",
            TotalBytes = 10,
            Versions =
            [
                new ConflictVersion
                {
                    Path = "b/teste.txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
                new ConflictVersion
                {
                    Path = "a/teste.txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            ],
        };

        var row = new ConflictGroupRow(empate);

        // Empate total de mtime e size: menor caminho em bytes UTF-8 ("a/…").
        Assert.Equal("a/teste.txt", row.SuggestedKeepPath);
    }
}
