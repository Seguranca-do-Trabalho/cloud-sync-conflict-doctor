using System.Text;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_64c4d4c0) — TextComparator (SPEC §16 Texto; ADR-0011 item 2; contratos.md
/// IDocumentComparator).
///
/// Normalização antes do diff (decisão do orquestrador): CRLF→LF; BOM (UTF-8/UTF-16)
/// reconhecido e ignorado — BOM divergente SOZINHO não torna os arquivos diferentes.
/// Comparação de linha exata pos-normalização, sem trim. Seleção por extensão
/// case-insensitive: .txt/.log/.ini/.cfg/.conf ⇒ texto; resto ⇒ binário.
/// </summary>
[Trait("Category", "Comparison")]
public class TextComparatorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _arquivos = new();
    private int _seq;

    public TextComparatorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t23-").FullName;
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
    public void Txt01_Identicos_EqualTrue_RegioesVazias()
    {
        var (left, right) = ParTexto("l1\nl2\n", "l1\nl2\n");

        var resultado = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("text", resultado.ComparatorKind);
        Assert.True(resultado.AreSemanticallyEqual);
        // Contrato do motor: idênticos ⇒ UMA região Equal cobrindo tudo
        // (Regions vazia é regra do BinaryFallbackComparator).
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Equal, regiao.Kind);
    }

    [Fact]
    public void Txt02_DivergenciaUnica_UmaRegiaoChangedEntreEquals()
    {
        var (left, right) = ParTexto("a\nb\nc\nd\n", "a\nB\nc\nd\n");

        var resultado = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal(3, resultado.Regions.Count);
        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(RegionKind.Changed, resultado.Regions[1].Kind);
        Assert.Equal((1, 1, 1, 1), (resultado.Regions[1].LeftStart, resultado.Regions[1].LeftCount, resultado.Regions[1].RightStart, resultado.Regions[1].RightCount));
    }

    [Fact]
    public void Txt03_CrlfDiferenteSoNoFimDeLinha_SaoIguais()
    {
        // Mesmo conteúdo lógico: left com LF, right com CRLF. Normalização CRLF→LF
        // precede o diff ⇒ iguais.
        var (left, right) = ParBytes(
            Encoding.UTF8.GetBytes("l1\nl2\n"),
            Encoding.UTF8.GetBytes("l1\r\nl2\r\n"));

        var resultado = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.True(resultado.AreSemanticallyEqual);
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Equal, regiao.Kind);
    }

    [Fact]
    public void Txt04_BomDivergenteSozinho_NaoTornaDiferente()
    {
        // Contrato XML-doc: BOM UTF-8 reconhecido e ignorado — presença/ausência de
        // BOM sozinha NÃO produz região.
        var comBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("x\ny\n")).ToArray();
        var semBom = Encoding.UTF8.GetBytes("x\ny\n");
        var (left, right) = ParBytes(comBom, semBom);

        var resultado = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.True(resultado.AreSemanticallyEqual);
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Equal, regiao.Kind);
    }

    [Fact]
    public void Txt05_ArquivoVazio_ContraUmaLinha_Added()
    {
        var (left, right) = ParTexto("", "unica\n");

        var resultado = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Added, regiao.Kind);
        Assert.Equal((0, 0, 0, 1), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Txt06_SemTrim_EspacoFinalEDiferenca()
    {
        // Comparação de linha exata pos-normalizacao: SEM trim.
        var (left, right) = ParTexto("valor \n", "valor\n");

        var resultado = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        Assert.Equal(RegionKind.Changed, Assert.Single(resultado.Regions).Kind);
    }

    [Fact]
    public void Txt07_Placeholder_ExcecaoAntesDeQualquerAbertura()
    {
        // Gate herdado (ADR-0011 item 4): o caminho NEM EXISTE em disco — qualquer
        // tentativa de abertura produziria FileNotFoundException. A prova de
        // "zero bytes lidos / zero aberturas" é receber PlaceholderReadException.
        var fantasma = Path.Combine(_dir, "nao-existe.txt");
        var existente = Path.Combine(_dir, "real.txt");
        File.WriteAllBytes(existente, Encoding.UTF8.GetBytes("conteudo\n"));
        _arquivos.Add(existente);

        var ex = Assert.Throws<PlaceholderReadException>(() => new TextComparator().Compare(
            Entrada(fantasma, 10, placeholder: true),
            Entrada(existente, new FileInfo(existente).Length),
            CancellationToken.None));
        Assert.Equal(fantasma, ex.EntryPath);

        // Placeholder no lado right também bloqueia antes da leitura do left.
        var ex2 = Assert.Throws<PlaceholderReadException>(() => new TextComparator().Compare(
            Entrada(existente, new FileInfo(existente).Length),
            Entrada(fantasma, 10, placeholder: true),
            CancellationToken.None));
        Assert.Equal(fantasma, ex2.EntryPath);
    }

    [Fact]
    public void Txt08_BomUtf16ReconhecEIgnoado_ContraUtf8_Iguais()
    {
        // BOM UTF-16 LE reconhecido pelo decoder e ignorado como diferença:
        // mesmo conteúdo lógico em codificações distintas ⇒ iguais.
        const string conteudo = "alpha\nbeta\n";
        var utf16Le = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(conteudo)).ToArray();
        var utf8Bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(conteudo)).ToArray();
        var (left, right) = ParBytes(utf16Le, utf8Bom);

        var resultado = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.True(resultado.AreSemanticallyEqual);
    }

    [Fact]
    public void Txt09_MesmaEntradaDuasExecucoes_ResultadoIdentico()
    {
        var (left, right) = ParTexto("a\nb\nc\n", "a\nX\nc\nd\n");

        var cmp = new TextComparator();
        var primeira = cmp.Compare(left, right, CancellationToken.None);
        var segunda = cmp.Compare(left, right, CancellationToken.None);

        // ComparisonResult carrega IReadOnlyList (igualdade por referência) —
        // determinismo se afirma campo a campo e região a região.
        Assert.Equal(primeira.ComparatorKind, segunda.ComparatorKind);
        Assert.Equal(primeira.AreSemanticallyEqual, segunda.AreSemanticallyEqual);
        Assert.Equal(primeira.Regions.Count, segunda.Regions.Count);
        for (int i = 0; i < primeira.Regions.Count; i++)
        {
            Assert.Equal(primeira.Regions[i], segunda.Regions[i]);
        }
    }

    /// <summary>Par de arquivos .txt com conteúdo UTF-8 sem BOM.</summary>
    private (FileEntry Left, FileEntry Right) ParTexto(string conteudoLeft, string conteudoRight) =>
        ParBytes(Encoding.UTF8.GetBytes(conteudoLeft), Encoding.UTF8.GetBytes(conteudoRight));

    /// <summary>Grava um par de arquivos .txt com bytes crus e devolve as entradas L0.</summary>
    private (FileEntry Left, FileEntry Right) ParBytes(byte[] bytesLeft, byte[] bytesRight)
    {
        var pl = Path.Combine(_dir, $"L{_seq}.txt");
        var pr = Path.Combine(_dir, $"R{_seq}.txt");
        _seq++;
        File.WriteAllBytes(pl, bytesLeft);
        File.WriteAllBytes(pr, bytesRight);
        _arquivos.Add(pl);
        _arquivos.Add(pr);
        return (Entrada(pl, bytesLeft.LongLength), Entrada(pr, bytesRight.LongLength));
    }

    /// <summary>Entrada L0 mínima para comparação (não-placeholder).</summary>
    internal static FileEntry Entrada(string path, long size, bool placeholder = false) => new()
    {
        Path = path,
        Size = size,
        MtimeUtc = DateTimeOffset.UnixEpoch,
        Attributes = FileAttributes.Normal,
        VolumeId = "vol-teste",
        FileId = path,
        IsPlaceholder = placeholder,
    };
}
