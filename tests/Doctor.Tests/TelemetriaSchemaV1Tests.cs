using Doctor.Gui.Engine;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// Ponte de leitura: expõe o validador do relatório sobre a telemetria crua,
/// para os testes de violação construírem contadores quebrados sem motor.
/// </summary>
internal static class ScanTelemetryValidatorExtensions
{
    internal static IEnumerable<string> ViolacoesDeInvariante(this ScanTelemetry t) =>
        new ScanReport { Telemetry = t }.ValidateTelemetryInvariants();
}

/// <summary>
/// t_2a116a88 — ciclo 1 (RED): telemetria da GUI em forma do schema v1
/// (docs/schema-report-v1.md §5): os 9 contadores exatos e as 4 invariantes.
/// O motor falso produz esses dados marcados DEMO, com cenários selecionáveis
/// cobrindo as bordas pedidas pelo card (nominal, sem duplicatas,
/// sem conflitos, só placeholders).
/// </summary>
public class TelemetriaSchemaV1Tests
{
    /// <summary>Invariantes normativas do schema v1 §5, avaliadas sobre o relatório.</summary>
    private static string[] ViolacoesInvariants(ScanReport r)
    {
        var violacoes = r.ValidateTelemetryInvariants().ToArray();

        // Guarda estrutural: o validador existe e avalia o próprio relatório.
        Assert.NotNull(violacoes);
        return violacoes;
    }

    [Fact]
    public void Cenario_nominal_produz_os_nove_contadores_exatos_do_demo()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal)
            .Scan(@"C:\Users\demo\OneDrive");

        var t = report.Telemetry;

        // 9 contadores EXATOS do schema v1, valores derivados do dataset DEMO:
        // 2 placeholders + 4 únicos (skipped) + 10 parcialmente hasheados
        // (8 que seguiram para L3 + 2 eliminados no L2) = 16 enumerados.
        Assert.Equal(16, t.FilesEnumerated);
        Assert.Equal(2, t.FilesPlaceholder);
        Assert.Equal(4, t.FilesSkipped);
        Assert.Equal(10, t.FilesPartialHashed);
        Assert.Equal(8, t.FilesFullHashed);

        // Janela L2: 64 KiB de cabeça + cauda por arquivo grande; arquivos <= 128 KiB
        // contam o arquivo inteiro uma única vez em bytes_read_partial.
        // (2×64 KiB) + 3×88.412 + 3×90.240 + 91.077 = 925.089 B.
        Assert.Equal(925_089L, t.BytesReadPartial);
        // Passe L3: arquivo inteiro dos sobreviventes (3 docx + 2 jpg + 3 versões).
        Assert.Equal(8_333_281L, t.BytesReadFull);
        Assert.Equal(t.BytesReadPartial + t.BytesReadFull, t.BytesRead);

        // Regra absoluta §6/§7: placeholder jamais é aberto.
        Assert.Equal(0L, t.PlaceholderBytesRead);
    }

    [Fact]
    public void Cenario_nominal_mantem_as_quatro_invariantes_do_schema()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.Nominal)
            .Scan(@"C:\Users\demo\OneDrive");

        Assert.Empty(ViolacoesInvariants(report));
    }

    [Theory]
    [InlineData(FakeScanEngine.ScanScenario.Nominal)]
    [InlineData(FakeScanEngine.ScanScenario.SemDuplicatas)]
    [InlineData(FakeScanEngine.ScanScenario.SemConflitos)]
    [InlineData(FakeScanEngine.ScanScenario.SoPlaceholders)]
    public void Todo_cenario_mantem_as_invariantes_de_telemetria(
        FakeScanEngine.ScanScenario cenario)
    {
        var report = new FakeScanEngine(cenario).Scan(@"C:\demo");

        Assert.Empty(report.ValidateTelemetryInvariants());

        var t = report.Telemetry;
        Assert.Equal(0L, t.PlaceholderBytesRead);
        Assert.Equal(t.BytesReadPartial + t.BytesReadFull, t.BytesRead);
        Assert.True(t.FilesFullHashed <= t.FilesPartialHashed);
    }

    [Fact]
    public void Cenario_sem_duplicatas_nao_tem_grupo_identico_e_resumo_zera()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.SemDuplicatas)
            .Scan(@"C:\demo");

        Assert.Empty(report.IdenticalDuplicates);
        Assert.NotEmpty(report.RealConflicts);
    }

    [Fact]
    public void Cenario_sem_conflitos_nao_tem_divergencia_e_resumo_zera()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.SemConflitos)
            .Scan(@"C:\demo");

        Assert.Empty(report.RealConflicts);
        Assert.NotEmpty(report.IdenticalDuplicates);
    }

    [Fact]
    public void Cenario_so_placeholders_tem_placeholders_e_nada_de_conteudo()
    {
        var report = new FakeScanEngine(FakeScanEngine.ScanScenario.SoPlaceholders)
            .Scan(@"C:\demo");

        Assert.NotEmpty(report.Placeholders);
        Assert.Empty(report.IdenticalDuplicates);
        Assert.Empty(report.RealConflicts);

        var t = report.Telemetry;
        Assert.True(t.FilesPlaceholder > 0);
        Assert.Equal(0L, t.BytesRead);          // nenhum byte de conteúdo lido
        Assert.Equal(0L, t.PlaceholderBytesRead); // e nenhum byte de placeholder
    }

    [Fact]
    public void Validador_acusa_violacao_quando_invariante_de_enumerados_e_quebrada()
    {
        var quebrada = new ScanTelemetry
        {
            FilesEnumerated = 10,
            FilesPlaceholder = 2,
            FilesSkipped = 3,
            FilesPartialHashed = 4, // 2+3+4 = 9 ≠ 10
            FilesFullHashed = 4,
            BytesRead = 100,
            BytesReadPartial = 40,
            BytesReadFull = 60,
            PlaceholderBytesRead = 0,
        };

        var violacoes = quebrada.ViolacoesDeInvariante();

        Assert.Contains(violacoes, v => v.Contains("files_enumerated"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Validador_acusa_placeholder_bytes_read_diferente_de_zero(long valor)
    {
        var quebrada = new ScanTelemetry
        {
            FilesEnumerated = 1,
            FilesPlaceholder = 0,
            FilesSkipped = 0,
            FilesPartialHashed = 1,
            FilesFullHashed = 1,
            BytesRead = 100,
            BytesReadPartial = 40,
            BytesReadFull = 60,
            PlaceholderBytesRead = valor,
        };

        var violacoes = quebrada.ViolacoesDeInvariante();

        Assert.Contains(violacoes, v => v.Contains("placeholder_bytes_read"));
    }

    [Fact]
    public void Validador_acusa_bytes_read_diferente_da_soma_parcial_mais_completa()
    {
        var quebrada = new ScanTelemetry
        {
            FilesEnumerated = 1,
            FilesPlaceholder = 0,
            FilesSkipped = 0,
            FilesPartialHashed = 1,
            FilesFullHashed = 1,
            BytesRead = 999, // ≠ 40 + 60
            BytesReadPartial = 40,
            BytesReadFull = 60,
            PlaceholderBytesRead = 0,
        };

        var violacoes = quebrada.ViolacoesDeInvariante();

        Assert.Contains(violacoes, v => v.Contains("bytes_read"));
    }
}
