using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G2 (t_87f625aa) — guarda textual GUIVM-02 estendida (docs/test-strategy.md):
/// NENHUMA string da GUI rotula destruição como "Apagar", "Excluir", "Delete" ou
/// sinônimo; o rótulo canônico único para ação sobre conteúdo do usuário é
/// "Mover para quarentena" (ADR-0002; §37 — segurança visualmente inequívoca).
/// Varredura completa: todos os .axaml do projeto de GUI + literais de string dos
/// ViewModels (fontes de texto visível ao usuário: Content, Text, Watermark).
/// </summary>
public class GuardaTextualGuiTests
{
    private static readonly string[] PalavrasProibidas =
    [
        // Português
        "apagar", "excluir", "descartar", "remover", "deletar", "eliminar",
        "limpar", "apagando", "excluindo", "removendo", "deletando",
        // Inglês (sinônimos comuns em UI)
        "delete", "erase", "remove", "discard", "destroy", "wipe", "purge", "trash",
    ];

    /// <summary>Exceções explícitas: contexto que nega o sentido destrutivo.</summary>
    private static readonly string[] FrasesPermitidas =
    [
        // A tela afirma que NADA é descartado — negação explícita, não oferta de destruição.
        "Nada é descartado de imediato",
        "Nenhum arquivo foi descartado",
    ];

    private static string RaizGui()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir!, "src", "Doctor.Gui");
    }

    private static IReadOnlyList<(string Arquivo, string Linha)> StringsDeInterface()
    {
        var raiz = RaizGui();
        var encontrados = new List<(string, string)>();

        // 1) Todos os .axaml da GUI (telas + recursos): atributos de texto visível.
        foreach (var axaml in Directory.EnumerateFiles(raiz, "*.axaml", SearchOption.AllDirectories))
        {
            var numeroLinha = 0;
            foreach (var linha in File.ReadLines(axaml))
            {
                numeroLinha++;
                foreach (var trecho in ExtrairValores(linha,
                             "Content=\"", "Text=\"", "Watermark=\"", "Title=\""))
                {
                    encontrados.Add(($"{Path.GetRelativePath(raiz, axaml)}:{numeroLinha}", trecho));
                }
            }
        }

        // 2) Literais de string dos ViewModels e code-behind (rótulos montados em código).
        foreach (var cs in Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories))
        {
            var numeroLinha = 0;
            foreach (var linha in File.ReadLines(cs))
            {
                numeroLinha++;
                foreach (var litral in ExtrairLiteraisCs(linha))
                {
                    encontrados.Add(($"{Path.GetRelativePath(raiz, cs)}:{numeroLinha}", litral));
                }
            }
        }

        return encontrados;
    }

    /// <summary>Extrai valores de atributos XAML de texto na linha.</summary>
    private static IEnumerable<string> ExtrairValores(string linha, params string[] atributos)
    {
        foreach (var atributo in atributos)
        {
            var inicio = 0;
            while (true)
            {
                var pos = linha.IndexOf(atributo, inicio, StringComparison.Ordinal);
                if (pos < 0)
                {
                    break;
                }

                var abre = pos + atributo.Length;
                var fecha = linha.IndexOf('"', abre);
                if (fecha > abre)
                {
                    yield return linha[abre..fecha];
                }

                inicio = pos + atributo.Length;
            }
        }
    }

    /// <summary>Extrai literais de string entre aspas simples/duplas em código C#.</summary>
    private static IEnumerable<string> ExtrairLiteraisCs(string linha)
    {
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(
                     linha, "\"((?:[^\"\\\\]|\\\\.)*)\"").Cast<System.Text.RegularExpressions.Match>())
        {
            if (m.Groups[1].Value.Length > 0)
            {
                yield return m.Groups[1].Value;
            }
        }
    }

    private static bool Violacao(string texto)
    {
        var permitido = FrasesPermitidas.Any(f =>
            texto.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (permitido)
        {
            return false;
        }

        return PalavrasProibidas.Any(p =>
            texto.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rotulo_canonico_de_destruicao_e_mover_para_quarentena()
    {
        var textos = StringsDeInterface();

        Assert.Contains(textos, t => t.Linha.Contains("Mover para quarentena", StringComparison.Ordinal));
    }

    [Fact]
    public void Nenhuma_string_da_gui_usa_vocabulario_destruivo()
    {
        var violacoes = StringsDeInterface()
            .Where(t => Violacao(t.Linha))
            .Select(t => $"{t.Arquivo}: \"{t.Linha}\"")
            .ToList();

        Assert.True(violacoes.Count == 0,
            $"GUIVM-02 violada — vocabulário destrutivo proibido encontrado:\n{string.Join("\n", violacoes)}");
    }
}
