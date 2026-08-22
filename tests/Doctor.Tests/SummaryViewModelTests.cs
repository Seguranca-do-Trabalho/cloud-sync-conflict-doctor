using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// t_2a116a88 — ciclo 2 (RED): o Resumo passa a ser um SummaryViewModel próprio
/// (GUIVM-01), respondendo as 5 perguntas do §15 a PARTIR dos dados do relatório
/// em forma schema v1 — nunca de números soltos.
/// </summary>
public class SummaryViewModelTests
{
    private static ScanReport ReportNominal() =>
        new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal).Scan(@"C:\demo");

    [Fact]
    public void SummaryViewModel_AnswersFiveQuestions_FromReportData()
    {
        var summary = new SummaryViewModel { Report = ReportNominal() };

        // 1. Arquivos encontrados = files_enumerated (dígitos crus, InvariantCulture).
        Assert.Equal("16", summary.FilesFound);

        // 2. Duplicatas idênticas = cópias redundantes: (3-1) + (2-1).
        Assert.Equal("3", summary.IdenticalDuplicates);

        // 3. Divergências reais = número de grupos com divergência real.
        Assert.Equal("1", summary.RealConflicts);

        // 4. Placeholders ignorados = files_placeholder (nunca abertos).
        Assert.Equal("2", summary.PlaceholdersIgnored);

        // 5. Espaço recuperável = fórmula explícita testada abaixo.
        // Perdedoras de duplicatas (4.556.064) + versões não mantidas do conflito
        // (179.489) = 4.735.553 B → 4,52 MiB com separador pt-BR fixo.
        Assert.Equal("4,52 MB", summary.RecoverableSpace);
    }

    /// <summary>
    /// A fórmula do espaço recuperável é testada explicitamente, por parcela:
    /// perdedoras das duplicatas idênticas + versões que não serão mantidas
    /// nos conflitos reais (sugestão determinística mtime → size → path).
    /// </summary>
    [Fact]
    public void Formula_do_espaco_recuperavel_soma_perdedoras_e_versoes_nao_mantidas()
    {
        var report = ReportNominal();

        // Parcela 1 — duplicatas idênticas: todas as cópias menos a mantida
        // (mantida = menor caminho em bytes UTF-8 dentro da classe).
        long perdedorasDuplicatas =
            report.IdenticalDuplicates.Sum(g => (long)(g.Files.Count - 1) * g.SizeBytes);
        Assert.Equal(2 * 1_048_576L + 1 * 2_458_912L, perdedorasDuplicatas); // 4.556.064

        // Parcela 2 — conflitos reais: soma das versões que NÃO serão mantidas
        // (mantida sugerida = mtime mais recente → "orcamento (DESKTOP-4K2F…)", 90.240 B).
        long naoMantidasConflitos = report.RealConflicts.Sum(g =>
            g.Versions.Where(v => !ReferenceEquals(v, ScanReport.SuggestVersionToKeep(g)))
                      .Sum(v => v.SizeBytes));
        Assert.Equal(88_412L + 91_077L, naoMantidasConflitos); // 179.489

        // Total esperado e propriedade do relatório coincidem com a fórmula.
        long esperado = perdedorasDuplicatas + naoMantidasConflitos;
        Assert.Equal(esperado, report.RecoverableBytes);
        Assert.Equal(4_556_064L + 179_489L, report.RecoverableBytes); // 4.735.553
    }

    [Fact]
    public void Sem_relatorio_as_cinco_respostas_sao_zero_seguras()
    {
        var summary = new SummaryViewModel();

        Assert.Equal("0", summary.FilesFound);
        Assert.Equal("0", summary.IdenticalDuplicates);
        Assert.Equal("0", summary.RealConflicts);
        Assert.Equal("0", summary.PlaceholdersIgnored);
        Assert.Equal("0 B", summary.RecoverableSpace);
    }

    [Theory]
    [InlineData(FakeScanEngine.ScanScenario.SemDuplicatas)]
    [InlineData(FakeScanEngine.ScanScenario.SemConflitos)]
    [InlineData(FakeScanEngine.ScanScenario.SoPlaceholders)]
    public void Bordas_do_motor_produzem_resumo_coerente(FakeScanEngine.ScanScenario cenario)
    {
        var report = new FakeScanEngine(cenario).Scan(@"C:\demo");
        var summary = new SummaryViewModel { Report = report };

        if (cenario == FakeScanEngine.ScanScenario.SemDuplicatas)
        {
            Assert.Equal("0", summary.IdenticalDuplicates);
            Assert.Equal("1", summary.RealConflicts);
        }

        if (cenario == FakeScanEngine.ScanScenario.SemConflitos)
        {
            Assert.Equal("3", summary.IdenticalDuplicates);
            Assert.Equal("0", summary.RealConflicts);
        }

        if (cenario == FakeScanEngine.ScanScenario.SoPlaceholders)
        {
            // Só placeholders: nada elegível para quarentena, nada recuperável.
            Assert.Equal("2", summary.PlaceholdersIgnored);
            Assert.Equal("0", summary.IdenticalDuplicates);
            Assert.Equal("0", summary.RealConflicts);
            Assert.Equal("0 B", summary.RecoverableSpace);
        }
    }
}
