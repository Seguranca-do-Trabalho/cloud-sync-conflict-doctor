using Xunit;

namespace Doctor.Tests;

/// <summary>
/// T06 — ciclo 4 (RED): regra visual de segurança do card, verificada contra o AXAML real.
/// Ação destrutiva é SEMPRE rotulada "mover para quarentena", nunca "apagar" (ADR-0002);
/// botão primário e botão destrutivo têm classes e cores distintas.
/// </summary>
public class RegraVisualSegurancaTests
{
    private static string LerMainWindowAxaml()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var caminho = Path.Combine(dir!, "src", "Doctor.Gui", "MainWindow.axaml");
        Assert.True(File.Exists(caminho), $"MainWindow.axaml não encontrado em {caminho}");
        return File.ReadAllText(caminho);
    }

    [Fact]
    public void Nenhuma_oferta_de_destruicao_usa_a_palavra_apagar()
    {
        var axaml = LerMainWindowAxaml();

        Assert.DoesNotContain("Apagar", axaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Excluir", axaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Delete", axaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Toda_acao_destrutiva_fala_em_mover_para_quarentena()
    {
        var axaml = LerMainWindowAxaml();

        Assert.Contains("Mover para quarentena", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Botao_primario_e_destrutivo_tem_cores_distintas()
    {
        var axaml = LerMainWindowAxaml();

        // Classes existem e são distintas.
        Assert.Contains("Button.primary", axaml, StringComparison.Ordinal);
        Assert.Contains("Button.destructive", axaml, StringComparison.Ordinal);

        // Cores explícitas distintas: azul primário vs vermelho destrutivo.
        Assert.Matches(@"Button\.primary""[^/]*#1F6FEB", axaml);
        Assert.Matches(@"Button\.destructive""[^/]*#C93C37", axaml);
    }
}
