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
        return LerArquivoGui("MainWindow.axaml");
    }

    private static string LerAppAxaml()
    {
        return LerArquivoGui("App.axaml");
    }

    private static string LerArquivoGui(string nomeArquivo)
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var caminho = Path.Combine(dir!, "src", "Doctor.Gui", nomeArquivo);
        Assert.True(File.Exists(caminho), $"{nomeArquivo} não encontrado em {caminho}");
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
        // G2: o sistema de estilos vive no App.axaml (centralizado); as telas apenas usam as classes.
        var app = LerAppAxaml();
        Assert.Contains("Button.primary", app, StringComparison.Ordinal);
        Assert.Contains("Button.destructive", app, StringComparison.Ordinal);
        Assert.Matches(@"Button\.primary""[^/]*#1F6FEB", app);
        Assert.Matches(@"Button\.destructive""[^/]*#C93C37", app);

        // As telas aplicam as classes nos botões.
        var axaml = LerMainWindowAxaml();
        Assert.Contains("Classes=\"primary\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"destructive\"", axaml, StringComparison.Ordinal);
    }

    // --- G2 (t_87f625aa): estilos nomeados CENTRALIZADOS no App.axaml ---

    [Fact]
    public void Estilos_nomeados_de_botao_sao_centralizados_no_App_axaml()
    {
        var app = LerAppAxaml();

        // Primário (azul), destrutivo (vermelho) e secundário (neutro) definidos no App.axaml,
        // para todas as telas herdarem o mesmo vocabulário visual (ADR-0009, §37).
        Assert.Matches(@"Style\s+Selector=""Button\.primary""[\s\S]*?#1F6FEB", app);
        Assert.Matches(@"Style\s+Selector=""Button\.destructive""[\s\S]*?#C93C37", app);
        Assert.Contains("Button.secondary", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Todo_botao_de_todas_as_telas_usa_o_vocabulario_de_classes()
    {
        // G2: nenhuma tela pode usar botão sem classe — o vocabulário
        // primário/destrutivo/secundário é único e completo (ADR-0009, §37).
        var linhas = LerMainWindowAxaml().Split('\n');

        for (var i = 0; i < linhas.Length; i++)
        {
            if (!linhas[i].Contains("<Button", StringComparison.Ordinal))
            {
                continue;
            }

            var usaVocabulario = linhas[i].Contains("Classes=\"primary\"", StringComparison.Ordinal)
                || linhas[i].Contains("Classes=\"destructive\"", StringComparison.Ordinal)
                || linhas[i].Contains("Classes=\"secondary\"", StringComparison.Ordinal);

            Assert.True(usaVocabulario,
                $"Botão sem classe de segurança na linha {i + 1}: {linhas[i].Trim()}");
        }
    }

    [Fact]
    public void Affordance_destrutiva_nao_e_compartilhada_com_navegacao_ou_comparacao()
    {
        // ADR-0009 (consequências) + §37: "Apagar" não pode se parecer com "comparar".
        // O botão destrutivo (classe destructive) nunca carrega texto de navegação/comparação,
        // e botões de navegação/comparação nunca usam a classe destrutiva.
        var axaml = LerMainWindowAxaml();

        foreach (var linha in axaml.Split('\n'))
        {
            var temClasseDestrutiva = linha.Contains("Classes=\"destructive\"", StringComparison.Ordinal);
            var rotuloNavegacao = linha.Contains("Content=\"Comparar", StringComparison.Ordinal)
                || linha.Contains("Content=\"Ver ", StringComparison.Ordinal)
                || linha.Contains("Content=\"Continuar", StringComparison.Ordinal)
                || linha.Contains("Content=\"Voltar", StringComparison.Ordinal)
                || linha.Contains("Content=\"Repensar", StringComparison.Ordinal)
                || linha.Contains("Content=\"Examinar", StringComparison.Ordinal)
                || linha.Contains("Content=\"Escanear", StringComparison.Ordinal)
                || linha.Contains("Content=\"Registrar", StringComparison.Ordinal);

            Assert.False(temClasseDestrutiva && rotuloNavegacao,
                $"Affordance destrutiva compartilhada com navegação/comparação: {linha.Trim()}");
        }
    }
}
