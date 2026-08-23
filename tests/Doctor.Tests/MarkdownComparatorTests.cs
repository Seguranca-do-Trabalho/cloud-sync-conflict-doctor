using System.Text;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T24 (t_f37457ba) — MarkdownComparator (SPEC §16 Markdown; ADR-0011 item 2;
/// contratos.md IDocumentComparator). Diff textual linha a linha sobre
/// <see cref="LcsDiff"/> com camada estrutural LEVE por cima: cabeçalhos ATX
/// (#{1..6}) FORA de cercas ``` e equilíbrio de cercas. Regra de refinamento:
/// região não-Equal cujo span (left ou right) contém cabeçalho e cujo conjunto
/// de cabeçalhos difere entre left/right ⇒ Kind=Changed. Diferença somente de
/// texto comum permanece Added/Removed. Sem AST, sem parser de markdown.
/// </summary>
[Trait("Category", "Comparison")]
public class MarkdownComparatorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _arquivos = new();
    private int _seq;

    public MarkdownComparatorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t24-md-").FullName;
    }

    public void Dispose()
    {
        foreach (var a in _arquivos)
        {
            File.Delete(a);
        }

        Directory.Delete(_dir);
    }

    [Fact]
    public void Md01_Identicos_EqualTrue_UmaRegiaoEqual()
    {
        var (left, right) = ParMarkdown("# Titulo\ntexto comum\n");

        var resultado = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("markdown", resultado.ComparatorKind);
        Assert.True(resultado.AreSemanticallyEqual);
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Equal, regiao.Kind);
    }

    [Fact]
    public void Md02_TextoComumMuda_ParSubstituto_PermaneceChanged()
    {
        // Mudança de texto comum (sem cabeçalho no span) mantém o par substituto
        // do LCS bruto: Changed 1:1 entre dois Equal.
        var (left, right) = ParMarkdown("intro\nlinha comum um\nfim\n", "intro\nlinha comum DOIS\nfim\n");

        var resultado = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("markdown", resultado.ComparatorKind);
        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(3, resultado.Regions.Count);
        Assert.Equal(RegionKind.Changed, resultado.Regions[1].Kind);
        Assert.Equal((1, 1, 1, 1), (resultado.Regions[1].LeftStart, resultado.Regions[1].LeftCount, resultado.Regions[1].RightStart, resultado.Regions[1].RightCount));
    }

    [Fact]
    public void Md03_HeadingAdicionado_Refinamento_ViraChanged()
    {
        // O LCS bruto emitiria Added para o cabeçalho inserido; como o span right
        // contém cabeçalho e o conjunto difere (∅ vs {Novo capitulo}), a região é
        // refinada para Changed preservando os spans.
        var (left, right) = ParMarkdown("intro\nfim\n", "intro\n# Novo capitulo\nfim\n");

        var resultado = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(3, resultado.Regions.Count);
        var regiao = resultado.Regions[1];
        Assert.Equal(RegionKind.Changed, regiao.Kind);
        Assert.Equal((1, 0, 1, 1), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Md04_HeadingRemovido_Refinamento_ViraChanged()
    {
        // Espelho da adição: remoção pura de cabeçalho vira Changed pelo mesmo critério.
        var (left, right) = ParMarkdown("intro\n# Capitulo X\nfim\n", "intro\nfim\n");

        var resultado = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(3, resultado.Regions.Count);
        var regiao = resultado.Regions[1];
        Assert.Equal(RegionKind.Changed, regiao.Kind);
        Assert.Equal((1, 1, 1, 0), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Md05_HeadingAlterado_Changed()
    {
        var (left, right) = ParMarkdown("# Titulo antigo\ncorpo\n", "# Titulo novo\ncorpo\n");

        var resultado = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.True(resultado.Regions.Count >= 1);
        Assert.Contains(resultado.Regions, r => r.Kind == RegionKind.Changed);
        var primeira = resultado.Regions[0];
        Assert.Equal((0, 1, 0, 1), (primeira.LeftStart, primeira.LeftCount, primeira.RightStart, primeira.RightCount));
    }

    [Fact]
    public void Md06_NivelDeHeadingMuda_ConjuntoDifere_Changed()
    {
        // "# T" e "## T" são linhas cruas distintas; mesmo texto após os cerilhas,
        // o CONJUNTO de cabeçalhos difere (nível entrou no conjunto pela linha crua).
        var (left, right) = ParMarkdown("# Mesmo texto\n", "## Mesmo texto\n");

        var resultado = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(RegionKind.Changed, Assert.Single(resultado.Regions).Kind);
    }

    [Fact]
    public void Md07_HeadingDentroDeCercaBalanceada_NaoConta()
    {
        // Cabeçalho-aparente REMOVIDO de dentro de uma cerca FECHADA: não é
        // cabeçalho para a camada estrutural ⇒ a região permanece Removed
        // (contaria como heading ⇒ viraria Changed numa implementação sem cerco).
        var (left, right) = ParMarkdown(
            "a\n```md\n# So dentro da cerca\n```\nb\n",
            "a\n```md\n```\nb\n");

        var resultado = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        // Estrutura: Equal(a + abertura da cerca) | Removed(# dentro da cerca) |
        // Equal(fechamento + b). O cabeçalho-aparente NÃO vira Changed.
        Assert.Equal(3, resultado.Regions.Count);
        Assert.Equal(RegionKind.Equal, resultado.Regions[0].Kind);
        var regiao = resultado.Regions[1];
        Assert.Equal(RegionKind.Removed, regiao.Kind);
        Assert.Equal((2, 1, 2, 0), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
        Assert.Equal(RegionKind.Equal, resultado.Regions[2].Kind);
    }

    [Fact]
    public void Md08_CercaNaoFechada_LinhasSeguintes_NaoSaoCabecalho()
    {
        // Cerca ABERTA e nunca fechada engole todo o restante do documento:
        // "# P1" e "# P2" no left estão DENTRO dela ⇒ nenhum cabeçalho conta.
        // Ambas as regiões de remoção permanecem Removed; uma leitura que
        // ignorasse cercas transformaria a remoção de "# P2" em Changed.
        var (left, right) = ParMarkdown(
            "a\n```\n# P1\n# P2\nz\n",
            "a\n# P1\nz\n");

        var resultado = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(5, resultado.Regions.Count);
        Assert.Equal(RegionKind.Equal, resultado.Regions[0].Kind);
        Assert.Equal(RegionKind.Removed, resultado.Regions[1].Kind);
        Assert.Equal(RegionKind.Equal, resultado.Regions[2].Kind);
        Assert.Equal(RegionKind.Removed, resultado.Regions[3].Kind);
        Assert.Equal(RegionKind.Equal, resultado.Regions[4].Kind);
    }

    [Fact]
    public void Md09_TextoComumAdicionado_SemCabecalhoNoSpan_PermaneceAdded()
    {
        // O refinamento NÃO dispara sem cabeçalho: inserção de texto comum fica Added.
        var (left, right) = ParMarkdown("a\nb\n", "a\nmeio\nb\n");

        var resultado = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(3, resultado.Regions.Count);
        Assert.Equal(RegionKind.Added, resultado.Regions[1].Kind);
        Assert.Equal((1, 0, 1, 1), (resultado.Regions[1].LeftStart, resultado.Regions[1].LeftCount, resultado.Regions[1].RightStart, resultado.Regions[1].RightCount));
    }

    [Fact]
    public void Md10_Placeholder_ExcecaoAntesDeQualquerAbertura()
    {
        // Gate herdado do 06.1 (ADR-0011 item 4): caminho fantasma — qualquer
        // tentativa de abertura lançaria FileNotFoundException; receber
        // PlaceholderReadException prova zero aberturas/zero bytes lidos.
        var fantasma = Path.Combine(_dir, "nao-existe.md");
        var existente = Path.Combine(_dir, "real.md");
        File.WriteAllBytes(existente, Encoding.UTF8.GetBytes("# t\n"));
        _arquivos.Add(existente);

        var ex = Assert.Throws<PlaceholderReadException>(() => new MarkdownComparator().Compare(
            TextComparatorTests.Entrada(fantasma, 10, placeholder: true),
            TextComparatorTests.Entrada(existente, new FileInfo(existente).Length),
            CancellationToken.None));
        Assert.Equal(fantasma, ex.EntryPath);

        var ex2 = Assert.Throws<PlaceholderReadException>(() => new MarkdownComparator().Compare(
            TextComparatorTests.Entrada(existente, new FileInfo(existente).Length),
            TextComparatorTests.Entrada(fantasma, 10, placeholder: true),
            CancellationToken.None));
        Assert.Equal(fantasma, ex2.EntryPath);
    }

    [Fact]
    public void Md11_MesmaEntradaDuasExecucoes_ResultadoIdentico()
    {
        var (left, right) = ParMarkdown("# Titulo\nintro\n# Secao\nfim\n", "# Titulo\nintro\n## Secao nova\n");

        var cmp = new MarkdownComparator();
        var primeira = cmp.Compare(left, right, CancellationToken.None);
        var segunda = cmp.Compare(left, right, CancellationToken.None);

        Assert.Equal(primeira.ComparatorKind, segunda.ComparatorKind);
        Assert.Equal(primeira.AreSemanticallyEqual, segunda.AreSemanticallyEqual);
        Assert.Equal(primeira.Regions.Count, segunda.Regions.Count);
        for (int i = 0; i < primeira.Regions.Count; i++)
        {
            Assert.Equal(primeira.Regions[i], segunda.Regions[i]);
        }
    }

    /// <summary>Par de arquivos .md com conteúdo UTF-8 sem BOM.</summary>
    private (FileEntry Left, FileEntry Right) ParMarkdown(string conteudoLeft, string? conteudoRight = null)
    {
        var bytesLeft = Encoding.UTF8.GetBytes(conteudoLeft);
        var bytesRight = Encoding.UTF8.GetBytes(conteudoRight ?? conteudoLeft);
        var pl = Path.Combine(_dir, $"L{_seq}.md");
        var pr = Path.Combine(_dir, $"R{_seq}.md");
        _seq++;
        File.WriteAllBytes(pl, bytesLeft);
        File.WriteAllBytes(pr, bytesRight);
        _arquivos.Add(pl);
        _arquivos.Add(pr);
        return (
            TextComparatorTests.Entrada(pl, bytesLeft.LongLength),
            TextComparatorTests.Entrada(pr, bytesRight.LongLength));
    }
}
