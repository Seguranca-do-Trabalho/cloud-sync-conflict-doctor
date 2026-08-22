using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// T06 — ciclo 2 (RED): a primeira tela pós-scan (Resumo) responde as 5 perguntas do §15.
/// Valores esperados vêm dos dados DEMO de FakeScanEngine.
/// </summary>
public class ResumoTests
{
    private static MainWindowViewModel NovoVmAposScan()
    {
        var vm = new MainWindowViewModel(new Doctor.Gui.Engine.FakeScanEngine());
        vm.ChosenFolder = @"C:\Users\demo\OneDrive";
        vm.StartScanCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void Resumo_responde_as_cinco_perguntas_da_spec()
    {
        var vm = NovoVmAposScan();

        // 1. Quantos arquivos foram encontrados? (inteiro invariante, sem separador de milhar)
        // Adaptado ao schema v1 (t_2a116a88): files_enumerated do dataset DEMO
        // (2 placeholders + 4 únicos + 10 parcialmente hasheados = 16).
        Assert.Equal("16", vm.SummaryFilesFound);

        // 2. Quantas duplicatas idênticas? (cópias redundantes: (3-1) + (2-1))
        Assert.Equal("3", vm.SummaryIdenticalDuplicates);

        // 3. Quantas divergências reais?
        Assert.Equal("1", vm.SummaryRealConflicts);

        // 4. Quantos placeholders foram ignorados?
        Assert.Equal("2", vm.SummaryPlaceholdersIgnored);

        // 5. Quanto espaço pode ser recuperado com segurança?
        // Fórmula do card t_2a116a88: perdedoras das duplicatas idênticas
        // (2×1MiB + 2.458.912 B = 4.556.064) + versões dos conflitos que não serão
        // mantidas (88.412 + 91.077 = 179.489) = 4.735.553 B / 1.048.576 = 4,52 MiB.
        Assert.Equal("4,52 MB", vm.SummaryRecoverableSpace);
    }
}

/// <summary>Formatação de bytes: invariante de locale (determinismo §3), legível ao consumidor.</summary>
public class FormatBytesTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(2048, "2 KB")]
    [InlineData(1_048_576, "1 MB")]
    [InlineData(4_556_064, "4,35 MB")] // 2*1MiB + 2458912 → exatamente o resumo DEMO
    [InlineData(1_610_612_736, "1,5 GB")]
    public void Formata_bytes_em_unidades_legiveis(long bytes, string esperado) =>
        Assert.Equal(esperado, MainWindowViewModel.FormatBytes(bytes));
}
