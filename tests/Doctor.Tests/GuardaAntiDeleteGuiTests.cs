using System.Text.RegularExpressions;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G3 (t_8d08c08b) — guarda estrutural complementar ao NDES-05 (que cobre src/**):
/// NENHUM código da GUI invoca API de deleção direta (File.Delete, Directory.Delete,
/// Move/Replace destrutivo, P/Invoke de remoção) — nem em comando, nem em code-behind.
/// A GUI só fala com IScanEngine/IQuarantineService-like (interface local DEMO);
/// a movimentação física para a quarentena é trabalho do EPIC 08, nunca da GUI.
/// </summary>
public class GuardaAntiDeleteGuiTests
{
    /// <summary>Padrões que caracterizam chamada destrutiva direta sobre o sistema
    /// de arquivos a partir do projeto de GUI.</summary>
    private static readonly string[] PadroesProibidos =
    [
        @"\bFile\.Delete\s*\(",
        @"\bDirectory\.Delete\s*\(",
        @"\bFileSystem\.DeleteFile\s*\(",
        @"\bFileSystem\.DeleteDirectory\s*\(",
        @"\.Delete\s*\(\s*\)",                       // FileInfo.Delete()/DirectoryInfo.Delete()
        @"\bFile\.Replace\s*\(",
        @"\bFile\.Move\s*\(",                        // mover arquivo é operação do serviço de quarentena
        @"\.MoveTo\s*\(",                            // FileInfo.MoveTo()/DirectoryInfo.MoveTo()
        @"\bDirectory\.Move\s*\(",
        @"\bSHFileOperation\b",
        @"\bIFileOperation\b",
        @"\bDllImport\b",                            // GUI DEMO não faz P/Invoke algum
    ];

    /// <summary>Exceções explícitas: chamadas legítimas que não destroem conteúdo.</summary>
    private static readonly string[] Excecoes =
    [
        // Directory.Move é proibido acima; nada mais coincide com estas frases:
        "Paths.Clear()",                              // fila interna da VM (ObservableCollection)
        ".Items.Clear()",                             // coleções de apresentação
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

    [Fact]
    public void Nenhum_codigo_da_gui_invoca_api_de_delecao_direta()
    {
        var raiz = RaizGui();
        var violacoes = new List<string>();

        foreach (var cs in Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories))
        {
            var numeroLinha = 0;
            foreach (var linha in File.ReadLines(cs))
            {
                numeroLinha++;
                foreach (var padrao in PadroesProibidos)
                {
                    if (Regex.IsMatch(linha, padrao, RegexOptions.IgnoreCase))
                    {
                        violacoes.Add(
                            $"{Path.GetRelativePath(raiz, cs)}:{numeroLinha} [{padrao}] {linha.Trim()}");
                    }
                }
            }
        }

        Assert.True(violacoes.Count == 0,
            "Guarda anti-delete violada — a GUI jamais toca no sistema de arquivos " +
            $"do usuário diretamente (movimentação é do EPIC 08):\n{string.Join("\n", violacoes)}");
    }

    /// <summary>Validação por mutação: a guarda tem de falhar se alguém inserir uma
    /// deleção. Roda o mesmo detector sobre um trecho contaminado — prova que o
    /// padrão reconhece a API, e que o teste verde não é verde por acaso.</summary>
    [Theory]
    [InlineData("File.Delete(caminho);")]
    [InlineData("Directory.Delete(pasta, recursive: true);")]
    [InlineData("origem.MoveTo(destino);")]
    [InlineData("[DllImport(\"shell32.dll\")] static extern int SHFileOperation(...);")]
    public void Detector_reconhece_chamadas_destrutivas_em_trecho_contaminado(string trecho)
    {
        var contaminado = PadroesProibidos.Any(p =>
            Regex.IsMatch(trecho, p, RegexOptions.IgnoreCase));

        Assert.True(contaminado,
            $"Detector da guarda anti-delete não reconheceu o trecho: {trecho}");
    }
}
