namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — PLH-04 / threat-model T-02: enumeração NUNCA atravessa reparse
/// points de diretório (junction/symlink/mount point) — regra 4 do ADR-0004.
/// Árvore real em tmp com ciclo de symlink: a varredura termina em tempo finito,
/// não desce pelo ciclo e registra o reparse como folha (ScanError) e continua.
/// </summary>
public class ReparseDirectoryGuardTests : IDisposable
{
    private readonly string _raiz;

    public ReparseDirectoryGuardTests()
    {
        _raiz = Path.Combine(Path.GetTempPath(), "cdt09-loop-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_raiz);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_raiz, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private FileAttributes SymlinkAttrs(string linkPath) =>
        File.GetAttributes(Path.Combine(_raiz, linkPath).Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void CicloDeSymlink_TerminaSemAtravessar_EContinuaOScan()
    {
        // Árvore: normal.txt antes do ciclo, ciclo a->b->a, normal.txt depois.
        // Se a enumeração atravessasse o ciclo, não terminaria em tempo finito.
        _ = Directory.CreateDirectory(Path.Combine(_raiz, "a"));
        _ = Directory.CreateDirectory(Path.Combine(_raiz, "b"));
        File.WriteAllText(Path.Combine(_raiz, "antes.txt"), "antes");
        File.WriteAllText(Path.Combine(_raiz, "a", "x.txt"), "xxx");
        File.WriteAllText(Path.Combine(_raiz, "b", "y.txt"), "yyy");
        File.WriteAllText(Path.Combine(_raiz, "depois.txt"), "depois");
        File.CreateSymbolicLink(Path.Combine(_raiz, "a", "loop"), _raiz + "/b");
        File.CreateSymbolicLink(Path.Combine(_raiz, "b", "loop"), _raiz + "/a");

        var resultado = new CrossPlatformEnumerator().Enumerate(_raiz, CancellationToken.None);

        var caminhos = resultado.Files.Select(f => f.Path).ToArray();
        Assert.Contains(caminhos, p => p.EndsWith("antes.txt", StringComparison.Ordinal));
        Assert.Contains(caminhos, p => p.EndsWith("depois.txt", StringComparison.Ordinal));
        Assert.Contains(caminhos, p => p.EndsWith("x.txt", StringComparison.Ordinal));
        Assert.Contains(caminhos, p => p.EndsWith("y.txt", StringComparison.Ordinal));

        // Exatamente os 4 arquivos reais: se o ciclo fosse atravessado, x/y
        // apareceriam duplicados a cada volta (e a varredura nem terminaria).
        Assert.Equal(4, caminhos.Count(p => p.EndsWith(".txt", StringComparison.Ordinal)));

        // Reparse de diretório registrado como folha (placeholders[]/erros) — nunca silencioso.
        Assert.Contains(resultado.Errors, e => e.Path.EndsWith(Path.Combine("a", "loop"), StringComparison.Ordinal));
        Assert.Contains(resultado.Errors, e => e.Path.EndsWith(Path.Combine("b", "loop"), StringComparison.Ordinal));

        // Zero bytes lidos: enumeração não lê conteúdo; telemetria coerente.
        Assert.Equal(0, resultado.Telemetry.PlaceholderBytesRead);
        Assert.Equal(resultado.Files.Count, resultado.Telemetry.FilesEnumerated);
    }

    [Fact]
    public void SymlinkDeDiretorio_ParaForaDaRaiz_NaoEhAtravessado()
    {
        // Threat-model T-02: symlink de diretório apontando para fora da raiz.
        // Conteúdo externo NUNCA aparece no relatório (privacidade local-first).
        var fora = Path.Combine(Path.GetTempPath(), "cdt09-fora-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(fora);
        File.WriteAllText(Path.Combine(fora, "segredo.txt"), "fora da raiz");
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "sub"));
            File.CreateSymbolicLink(Path.Combine(_raiz, "sub", "escape"), fora);

            var resultado = new CrossPlatformEnumerator().Enumerate(_raiz, CancellationToken.None);

            Assert.DoesNotContain(resultado.Files, f => f.Path.Contains("segredo", StringComparison.Ordinal));
            Assert.Contains(resultado.Errors, e => e.Path.EndsWith("escape", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fora, recursive: true);
        }
    }

    [Fact]
    public void SymlinkDeArquivo_EntraMarcadoComoPlaceholder_NuncaAtravessado()
    {
        // SPEC §6: reparse points não são seguidos. No POSIX, symlink de ARQUIVO é o
        // análogo: entra na lista Level 0 marcado, sem abrir o alvo.
        File.WriteAllText(Path.Combine(_raiz, "real.txt"), "conteudo real");
        File.CreateSymbolicLink(Path.Combine(_raiz, "atalho.txt"), Path.Combine(_raiz, "real.txt"));

        var resultado = new CrossPlatformEnumerator().Enumerate(_raiz, CancellationToken.None);

        var atalho = Assert.Single(resultado.Files, f => f.Path.EndsWith("atalho.txt", StringComparison.Ordinal));
        Assert.True(atalho.IsPlaceholder);
        Assert.Equal(PlaceholderKind.ReparsePoint, atalho.PlaceholderKind);
        Assert.Contains(resultado.Files, f => f.Path.EndsWith("real.txt", StringComparison.Ordinal));
    }
}
