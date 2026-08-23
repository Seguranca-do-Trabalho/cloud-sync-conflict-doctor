namespace Doctor.Tests;

using Doctor.Core;
using Xunit;

/// <summary>
/// SEG-04 e SEG-05 (T-02 do threat-model, regra R3): defesas contra junction/symlink loop
/// e contra vazamento de conteúdo externo pela enumeração Level 0.
///
/// SEG-04 — <see cref="Security_JunctionLoop_TerminatesWithoutDescent"/>: ciclo a→b→a
/// via symlinks de diretório termina em tempo finito; os symlinks entram como folhas
/// (registrados em Errors com IsReparsePoint=true), nunca como ponto de descida.
///
/// SEG-05 — <see cref="Security_ReparseDir_PointingOutsideRoot_NotEntered"/>: symlink de
/// diretório apontando para fora da raiz escaneada nunca expõe conteúdo externo no
/// relatório nem na lista de arquivos; a entrada do symlink é folha registrada em Errors.
/// </summary>
public sealed class SecurityJunctionSymlinkTests : IDisposable
{
    private readonly string _raiz;
    private readonly string _foraDaRaiz;

    public SecurityJunctionSymlinkTests()
    {
        _raiz = Path.Combine(Path.GetTempPath(), $"seg04-05-{Guid.NewGuid():N}");
        _foraDaRaiz = Path.Combine(Path.GetTempPath(), $"seg04-05-fora-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_raiz);
        Directory.CreateDirectory(_foraDaRaiz);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _raiz, _foraDaRaiz })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    // =====================================================================
    // SEG-04 — Security_JunctionLoop_TerminatesWithoutDescent (T-02, P1)
    // =====================================================================
    [Fact]
    [Trait("Category", "Security")]
    public void Security_JunctionLoop_TerminatesWithoutDescent()
    {
        // Montar ciclo real a->b->a com symlinks de diretório (simula junction no Linux).
        var pastaA = Path.Combine(_raiz, "a");
        var pastaB = Path.Combine(_raiz, "b");
        Directory.CreateDirectory(pastaA);
        Directory.CreateDirectory(pastaB);

        File.WriteAllText(Path.Combine(pastaA, "x.txt"), "conteudo-de-a");
        File.WriteAllText(Path.Combine(pastaB, "y.txt"), "conteudo-de-b");

        // Symlinks de diretório em ciclo: a/link -> b, b/link -> a.
        var linkAB = Path.Combine(pastaA, "link");
        var linkBA = Path.Combine(pastaB, "link");
        File.CreateSymbolicLink(linkAB, pastaB);
        File.CreateSymbolicLink(linkBA, pastaA);

        // Executar enumeração Level 0 completa (pipeline de produção).
        var enumerador = new OrderedFileEnumerator(new CrossPlatformEnumerator());
        var resultado = enumerador.Enumerate(_raiz, CancellationToken.None);

        // 1. Termina em tempo finito: chegamos aqui = o scan não looping.
        // 2. Arquivos reais presentes e corretos.
        var caminhos = resultado.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Contains(caminhos, p => p.EndsWith("x.txt", StringComparison.Ordinal));
        Assert.Contains(caminhos, p => p.EndsWith("y.txt", StringComparison.Ordinal));

        // 3. Symlinks de diretório entram como FOLHAS registradas em Errors, nunca como
        //    entrada na lista Files (não há descida).
        Assert.DoesNotContain(resultado.Files, f => f.Path.Contains("link", StringComparison.Ordinal));
        Assert.Contains(resultado.Errors, e => e.Path.EndsWith(Path.Combine("a", "link"), StringComparison.Ordinal));
        Assert.Contains(resultado.Errors, e => e.Path.EndsWith(Path.Combine("b", "link"), StringComparison.Ordinal));

        // 4. Telemetria coerente: nada foi lido (Level 0 não lê conteúdo).
        Assert.Equal(0, resultado.Telemetry.BytesRead);
        Assert.Equal(0, resultado.Telemetry.PlaceholderBytesRead);
        Assert.Equal(resultado.Files.Count, resultado.Telemetry.FilesEnumerated);
    }

    // =====================================================================
    // SEG-05 — Security_ReparseDir_PointingOutsideRoot_NotEntered (T-02, P1)
    // =====================================================================
    [Fact]
    [Trait("Category", "Security")]
    public void Security_ReparseDir_PointingOutsideRoot_NotEntered()
    {
        // Criar arquivo REAL dentro da raiz (para provar que o scan consegue ver conteúdo interno).
        var arquivoInterno = Path.Combine(_raiz, "interno.txt");
        File.WriteAllText(arquivoInterno, "sou-interno");

        // Criar arquivo FORA da raiz (isca de privacidade).
        var arquivoExterno = Path.Combine(_foraDaRaiz, "secreto.txt");
        File.WriteAllText(arquivoExterno, "NÃO-deve-appears-no-relatorio");

        // Symlink de diretório dentro da raiz apontando PARA FORA.
        var linkExterno = Path.Combine(_raiz, "link-externo");
        File.CreateSymbolicLink(linkExterno, _foraDaRaiz);

        // Executar enumeração Level 0.
        var enumerador = new OrderedFileEnumerator(new CrossPlatformEnumerator());
        var resultado = enumerador.Enumerate(_raiz, CancellationToken.None);

        // 1. Arquivo interno presente.
        Assert.Contains(resultado.Files, f => f.Path.Equals(arquivoInterno, StringComparison.Ordinal));

        // 2. Conteúdo externo NUNCA aparece no relatório (nem em Files, nem em Errors).
        Assert.DoesNotContain(resultado.Files, f => f.Path.Contains(_foraDaRaiz, StringComparison.Ordinal));
        Assert.DoesNotContain(resultado.Errors, e => e.Path.Contains(_foraDaRaiz, StringComparison.Ordinal));

        // 3. O symlink de diretório entra como folha registrada (erro de reparse), não como
        //    ponto de descida.
        Assert.Contains(resultado.Errors, e => e.Path.Equals(linkExterno, StringComparison.Ordinal));
        Assert.DoesNotContain(resultado.Files, f => f.Path.Equals(linkExterno, StringComparison.Ordinal));

        // 4. Arquivo externo permanece intocado (prova de que o scan não acessou o alvo).
        Assert.Equal("NÃO-deve-appears-no-relatorio", File.ReadAllText(arquivoExterno));

        // 5. Telemetria limpa.
        Assert.Equal(0, resultado.Telemetry.PlaceholderBytesRead);
    }
}
