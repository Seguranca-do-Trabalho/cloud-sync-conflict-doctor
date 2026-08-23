using System.Text;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T24 (t_f37457ba) — CsvComparator (SPEC §16 CSV; ADR-0011 item 2; contratos.md
/// IDocumentComparator). Parsing subconjunto RFC4180 (aspas duplas, aspa escapada
/// ""); delimitador detectado por contagem na primeira linha não-vazia de cada
/// arquivo entre ',' ';' '\t'; delimitadores DIFERENTES entre os arquivos ⇒
/// TODAS as linhas Changed (diferença estrutural). Sem inferência de header.
/// Alinhamento por <see cref="LcsDiff"/> sobre a LINHA CRUA normalizada; par
/// Changed ⇒ comparação célula a célula: células iguais pós-parse ⇒ região volta
/// a Equal (aspas redundantes não são diferença semântica); número de células
/// diferente ⇒ Changed na linha inteira. Sem reordenação de linhas.
/// </summary>
[Trait("Category", "Comparison")]
public class CsvComparatorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _arquivos = new();
    private int _seq;

    public CsvComparatorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t24-csv-").FullName;
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
    public void Csv01_Identicos_EqualTrue_UmaRegiaoEqual()
    {
        var (left, right) = ParCsv("a;b;c\n1;2;3\n");

        var resultado = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("csv", resultado.ComparatorKind);
        Assert.True(resultado.AreSemanticallyEqual);
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Equal, regiao.Kind);
    }

    [Fact]
    public void Csv02_CelulaUnicaMuda_ParChangedMinimo()
    {
        // Uma célula alterada numa tabela de 3 linhas ⇒ UMA região Changed 1:1
        // na linha divergente, cercada por um único bloco Equal.
        var (left, right) = ParCsv(
            "colA;colB\num;dois\ntres;quatro\n",
            "colA;colB\num;dois\ntres;QUATRO\n");

        var resultado = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(2, resultado.Regions.Count);
        Assert.Equal(RegionKind.Equal, resultado.Regions[0].Kind);
        Assert.Equal((0, 2, 0, 2), (resultado.Regions[0].LeftStart, resultado.Regions[0].LeftCount, resultado.Regions[0].RightStart, resultado.Regions[0].RightCount));
        var regiao = resultado.Regions[1];
        Assert.Equal(RegionKind.Changed, regiao.Kind);
        Assert.Equal((2, 1, 2, 1), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Csv03_LinhaAdicionada_Added()
    {
        var (left, right) = ParCsv("l1\nl2\n", "l1\nnovo\nl2\n");

        var resultado = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(3, resultado.Regions.Count);
        var regiao = resultado.Regions[1];
        Assert.Equal(RegionKind.Added, regiao.Kind);
        Assert.Equal((1, 0, 1, 1), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Csv04_LinhaRemovida_Removed()
    {
        var (left, right) = ParCsv("l1\nvelha\nl2\n", "l1\nl2\n");

        var resultado = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(3, resultado.Regions.Count);
        var regiao = resultado.Regions[1];
        Assert.Equal(RegionKind.Removed, regiao.Kind);
        Assert.Equal((1, 1, 1, 0), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Csv05_QuantidadeDeColunasDifere_LinhaInteiraChanged()
    {
        // Par cru divergente cujas células pós-parse têm contagens diferentes:
        // permanece Changed cobrindo a LINHA inteira (nunca região sub-linha).
        var (left, right) = ParCsv("um;dois\n", "um;dois;tres\n");

        var resultado = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Changed, regiao.Kind);
        Assert.Equal((0, 1, 0, 1), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Csv06_AspasRedundantes_CelulasIguais_SemanticamenteIguais()
    {
        // Comparações célula a célula: `"a";"b"` e `a;b` produzem as MESMAS
        // células pós-parse ⇒ a região Changed crua VOLTA a Equal e o par é
        // semanticamente igual (aspas são sintaxe, não conteúdo).
        var (left, right) = ParCsv("\"a\";\"b\"\n", "a;b\n");

        var resultado = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.True(resultado.AreSemanticallyEqual);
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Equal, regiao.Kind);
        Assert.Equal((0, 1, 0, 1), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Csv07_CamposComVirgulaAspasEscapadas_IguaisQuandoConteudoIgual()
    {
        // Vírgula e aspa escapada "" DENTRO de campo citado: o parser precisa
        // preservar o conteúdo literal para declarar igualdade verdadeira...
        const string conteudo = "\"x,y\";\"ele disse \"\"oi\"\"\"\n";
        var (left, right) = ParCsv(conteudo);

        var resultadoIgual = new CsvComparator().Compare(left, right, CancellationToken.None);
        Assert.True(resultadoIgual.AreSemanticallyEqual);

        // ...e enxergar a diferença REAL dentro do campo citado como Changed.
        var (l2, r2) = ParCsv(
            "\"x,y\";z\n",
            "\"x,z\";z\n");
        var resultadoDiferente = new CsvComparator().Compare(l2, r2, CancellationToken.None);

        Assert.False(resultadoDiferente.AreSemanticallyEqual);
        Assert.Equal(RegionKind.Changed, Assert.Single(resultadoDiferente.Regions).Kind);
    }

    [Fact]
    public void Csv08_DelimitadoresDiferentesEntreArquivos_TodasAsLinhasChanged()
    {
        // ',' no left, ';' no right: diferença estrutural ⇒ NENHUMA região Equal;
        // todas as linhas dos dois lados ficam em regiões Changed.
        var (left, right) = ParCsv("a,b\n1,2\n", "x;y\n3;4\n");

        var resultado = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("csv", resultado.ComparatorKind);
        Assert.False(resultado.AreSemanticallyEqual);
        Assert.DoesNotContain(resultado.Regions, r => r.Kind == RegionKind.Equal);
        Assert.Equal(2, resultado.Regions.Sum(r => r.LeftCount));
        Assert.Equal(2, resultado.Regions.Sum(r => r.RightCount));
        Assert.All(resultado.Regions, r => Assert.Equal(RegionKind.Changed, r.Kind));
    }

    [Fact]
    public void Csv09_DelimitadorDetectadoNaPrimeiraLinhaNaoVazia_BlankInicialIgnorada()
    {
        // Detecção olha a PRIMEIRA linha NÃO-VAZIA: o blank inicial do left não
        // pode derrubar a contagem para zero (o que escolheria ',' default e
        // dispararia o caminho estrutural all-Changed contra o ';' do right).
        // Caminho correto: ambos ';' ⇒ diff normal de linhas com bloco Equal.
        var (left, right) = ParCsv("\nk1;k2\nv1;v2\n", "k1;k2\nv1;V2\n");

        var resultado = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(3, resultado.Regions.Count);
        // A linha em branco existe só no left ⇒ Removed; o par k1;k2 casa (Equal);
        // v1;v2 vs v1;V2 é o par Changed com células divergentes.
        Assert.Equal(RegionKind.Removed, resultado.Regions[0].Kind);
        Assert.Equal(RegionKind.Equal, resultado.Regions[1].Kind);
        Assert.Equal((1, 1, 0, 1), (resultado.Regions[1].LeftStart, resultado.Regions[1].LeftCount, resultado.Regions[1].RightStart, resultado.Regions[1].RightCount));
        Assert.Equal(RegionKind.Changed, resultado.Regions[2].Kind);
    }

    [Fact]
    public void Csv10_Placeholder_ExcecaoAntesDeQualquerAbertura()
    {
        // Gate herdado do 06.1 (ADR-0011 item 4): caminho fantasma — qualquer
        // tentativa de abertura lançaria FileNotFoundException; receber
        // PlaceholderReadException prova zero aberturas/zero bytes lidos.
        var fantasma = Path.Combine(_dir, "nao-existe.csv");
        var existente = Path.Combine(_dir, "real.csv");
        File.WriteAllBytes(existente, Encoding.UTF8.GetBytes("a;b\n"));
        _arquivos.Add(existente);

        var ex = Assert.Throws<PlaceholderReadException>(() => new CsvComparator().Compare(
            TextComparatorTests.Entrada(fantasma, 10, placeholder: true),
            TextComparatorTests.Entrada(existente, new FileInfo(existente).Length),
            CancellationToken.None));
        Assert.Equal(fantasma, ex.EntryPath);

        var ex2 = Assert.Throws<PlaceholderReadException>(() => new CsvComparator().Compare(
            TextComparatorTests.Entrada(existente, new FileInfo(existente).Length),
            TextComparatorTests.Entrada(fantasma, 10, placeholder: true),
            CancellationToken.None));
        Assert.Equal(fantasma, ex2.EntryPath);
    }

    [Fact]
    public void Csv11_MesmaEntradaDuasExecucoes_ResultadoIdentico()
    {
        var (left, right) = ParCsv(
            "a;b\num;dois\ntres;quatro\n",
            "a;b\num;DOIS\ntres;quatro\nextra\n");

        var cmp = new CsvComparator();
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

    /// <summary>Par de arquivos .csv com conteúdo UTF-8 sem BOM.</summary>
    private (FileEntry Left, FileEntry Right) ParCsv(string conteudoLeft, string? conteudoRight = null)
    {
        var bytesLeft = Encoding.UTF8.GetBytes(conteudoLeft);
        var bytesRight = Encoding.UTF8.GetBytes(conteudoRight ?? conteudoLeft);
        var pl = Path.Combine(_dir, $"L{_seq}.csv");
        var pr = Path.Combine(_dir, $"R{_seq}.csv");
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
