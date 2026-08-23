using System.Text.RegularExpressions;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// NDES-05 (t_b126bdf6) — guarda estática sobre o texto-fonte do produto.
/// NENHUM .cs sob src/ (excluídos tests/, obj/, bin/) pode conter APIs de deleção
/// permanente: File.Delete, Directory.Delete, DeleteFile/DeleteFileW (P/Invoke),
/// FILE_DISPOSITION_INFO ou SetFileInformationByHandle(FileDispositionInfo*),
/// FILE_RENAME_INFO (exceto movimentação de quarentena, que ainda não existe na v1).
/// Allowlist vazia na v1 (conforme decisão do orquestrador).
/// Complementa ADR-0002 item 5 e a revisão estática do reviewer (GATE 3).
/// </summary>
[Xunit.Trait("Category", "Safety")]
public class NdesSourceGuardTests
{
    // Regras: cada regex deve capturar chamada perigosa.
    // Grupo "api" identifica qual regra disparou para a mensagem de falha.
    private static readonly Regex[] Padroes =
    [
        // APIs gerenciadas diretas.
        new Regex(@"\bFile\.Delete\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"\bDirectory\.Delete\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // P/Invoke Windows (remover arquivo/diretório).
        new Regex(@"\bDeleteFile\s*[W]?\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"\bDeleteFileW\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // FILE_DISPOSITION_INFO / SetFileInformationByHandle(FileDispositionInfo*).
        new Regex(@"\bFILE_DISPOSITION_INFO\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(
            @"SetFileInformationByHandle\s*\([^,]+,\s*FileDispositionInfo\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // FILE_RENAME_INFO (rename é permitido apenas dentro do mover de quarentena,
        // ainda não implantado na v1; allowlist vazia).
        new Regex(@"\bFILE_RENAME_INFO\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    private static string RaizSrc()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(dir!, "src");
    }

    private static IReadOnlyList<string> VarrerTextos()
    {
        var raiz = RaizSrc();
        var violacoes = new List<string>();

        if (!Directory.Exists(raiz))
        {
            return ["src/ não existe (estrutura do repo alterada?)"];
        }

        foreach (var cs in Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories))
        {
            // Exclui code-generated, obj e bin.
            if (cs.Contains("/obj/", StringComparison.Ordinal) ||
                cs.Contains("\\obj\\", StringComparison.Ordinal) ||
                cs.Contains("/bin/", StringComparison.Ordinal) ||
                cs.Contains("\\bin\\", StringComparison.Ordinal))
            {
                continue;
            }

            var relPath = Path.GetRelativePath(raiz, cs);
            var numeroLinha = 0;
            foreach (var linha in File.ReadLines(cs))
            {
                numeroLinha++;
                foreach (var padrao in Padroes)
                {
                    if (padrao.IsMatch(linha))
                    {
                        violacoes.Add($"{relPath}:{numeroLinha} [{padrao}] {linha.Trim()}");
                    }
                }
            }
        }

        return violacoes;
    }

    [Fact]
    public void Zero_ocorrencias_de_api_delete_no_texto_fonte_src()
    {
        var violacoes = VarrerTextos();
        Assert.True(violacoes.Count == 0,
            $"NDES-05 violada — APIs de deleção permanente encontradas em src/. " +
            $"Allowlist vazia na v1.\n{string.Join("\n", violacoes)}");
    }

    /// <summary>Prova dupla: mostra que o detector reconhece e rejeita violações reais.</summary>
    [Theory]
    [InlineData("File.Delete(path);")]
    [InlineData("Directory.Delete(dir, true);")]
    [InlineData("DeleteFileW(name);")]
    [InlineData("var fi = new FILE_DISPOSITION_INFO();")]
    [InlineData("SetFileInformationByHandle(h, FileDispositionInfo, ...);")]
    [InlineData("var ri = new FILE_RENAME_INFO();")]
    public void Detector_pega_violacao_em_trecho_contaminado(string trecho)
    {
        // Injeta trecho contaminado num arquivo temporário sob src/
        var raiz = RaizSrc();
        var fake = Path.Combine(raiz, "__guard_prova_noesse_arquivo_21847.cs");
        try
        {
            File.WriteAllText(fake, trecho);
            var violacoes = VarrerTextos();
            Assert.NotEmpty(violacoes);
            // A mensagem mostra caminho relativo a src/ + número de linha.
            Assert.Contains("__guard_prova_noesse_arquivo_21847.cs:",
                string.Join(" ", violacoes));
        }
        finally
        {
            // Remove imediatamente; NDES-05 do commit limpo passa.
            if (File.Exists(fake))
            {
                File.Delete(fake);
            }
        }
    }
}
