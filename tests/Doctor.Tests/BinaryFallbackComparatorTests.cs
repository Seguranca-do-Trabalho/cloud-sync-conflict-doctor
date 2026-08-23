using System.Text;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_64c4d4c0) — BinaryFallbackComparator (SPEC §16; ADR-0011 item 1: tipo
/// desconhecido ⇒ binário de fallback apontando os primeiros bytes divergentes;
/// contratos.md IDocumentComparator).
///
/// Decisão do orquestrador: leitura em blocos fixos de 64 KiB; primeira divergência
/// ⇒ DiffRegion Changed com LeftStart=RightStart=offset e LeftCount=RightCount=1;
/// idênticos ⇒ AreSemanticallyEqual=true e Regions vazia.
/// </summary>
[Trait("Category", "Comparison")]
public class BinaryFallbackComparatorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _arquivos = new();
    private int _seq;

    public BinaryFallbackComparatorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t23-bin-").FullName;
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
    public void Bin01_Identicos_Iguais_RegioesVazia()
    {
        var (left, right) = ParBytes(new byte[] { 0x00, 0x01, 0xFF, 0x7F }, new byte[] { 0x00, 0x01, 0xFF, 0x7F });

        var resultado = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("binary", resultado.ComparatorKind);
        Assert.True(resultado.AreSemanticallyEqual);
        Assert.Empty(resultado.Regions);
    }

    [Fact]
    public void Bin02_PrimeiraDivergencia_ChangedNoOffset_Count1()
    {
        var bytesL = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var bytesR = new byte[] { 0x01, 0x02, 0x99, 0x04 };
        var (left, right) = ParBytes(bytesL, bytesR);

        var resultado = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Changed, regiao.Kind);
        Assert.Equal((2, 1, 2, 1), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Bin03_PrimeiroByte_DivergeNoOffsetZero()
    {
        var (left, right) = ParBytes(new byte[] { 0xAA, 0xBB }, new byte[] { 0xCC, 0xBB });

        var resultado = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal((0, 1, 0, 1), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Bin04_ArquivoMaiorQueUmBloco_DivergenciaNoSegundoBloco()
    {
        // Blocos de 64 KiB: divergência no offset 70_000 cai no SEGUNDO bloco —
        // prova que a leitura em blocos não para no primeiro.
        const int offset = 70_000;
        var bytesL = new byte[80_000];
        var bytesR = new byte[80_000];
        Array.Fill(bytesL, (byte)0x42);
        Array.Fill(bytesR, (byte)0x42);
        bytesR[offset] = 0x24;

        var (left, right) = ParBytes(bytesL, bytesR);

        var resultado = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Changed, regiao.Kind);
        Assert.Equal((offset, 1, offset, 1), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));
    }

    [Fact]
    public void Bin05_PrefixoComumIgal_TamanhosDiferentes_ExcedenteERegiaoUnilateral()
    {
        // Sem byte divergente no trecho comum, porém conteúdos diferentes
        // (tamanhos distintos): o excedente é Reported como Removed (somente left)
        // ou Added (somente right) — vocabulário RegionKind, decisão do orquestrador.
        var (left, right) = ParBytes(new byte[] { 0x01, 0x02, 0x03 }, new byte[] { 0x01, 0x02 });

        var resultado = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        Assert.False(resultado.AreSemanticallyEqual);
        var regiao = Assert.Single(resultado.Regions);
        Assert.Equal(RegionKind.Removed, regiao.Kind);
        Assert.Equal((2, 1, 2, 0), (regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount));

        var (left2, right2) = ParBytes(new byte[] { 0x01, 0x02 }, new byte[] { 0x01, 0x02, 0x03 });
        var resultado2 = new BinaryFallbackComparator().Compare(left2, right2, CancellationToken.None);
        var regiao2 = Assert.Single(resultado2.Regions);
        Assert.Equal(RegionKind.Added, regiao2.Kind);
        Assert.Equal((2, 0, 2, 1), (regiao2.LeftStart, regiao2.LeftCount, regiao2.RightStart, regiao2.RightCount));
    }

    [Fact]
    public void Bin06_Placeholder_ExcecaoAntesDeQualquerAbertura()
    {
        // Gate herdado (ADR-0011 item 4): caminho nem existe — qualquer abertura
        // daria FileNotFoundException; receber PlaceholderReadException prova zero
        // bytes lidos de placeholder.
        var fantasma = Path.Combine(_dir, "nao-existe.bin");
        var existente = Path.Combine(_dir, "real.bin");
        File.WriteAllBytes(existente, new byte[] { 0x01 });
        _arquivos.Add(existente);

        var ex = Assert.Throws<PlaceholderReadException>(() => new BinaryFallbackComparator().Compare(
            Entrada(fantasma, 10, placeholder: true),
            Entrada(existente, 1),
            CancellationToken.None));
        Assert.Equal(fantasma, ex.EntryPath);
    }

    [Fact]
    public void Bin07_MesmaEntradaDuasExecucoes_ResultadoIdentico()
    {
        var (left, right) = ParBytes(new byte[] { 0x00, 0xFF, 0x10 }, new byte[] { 0x00, 0xEE, 0x10 });

        var cmp = new BinaryFallbackComparator();
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

    /// <summary>Grava um par de arquivos .bin com bytes crus e devolve as entradas L0.</summary>
    private (FileEntry Left, FileEntry Right) ParBytes(byte[] bytesLeft, byte[] bytesRight)
    {
        var pl = Path.Combine(_dir, $"L{_seq}.bin");
        var pr = Path.Combine(_dir, $"R{_seq}.bin");
        _seq++;
        File.WriteAllBytes(pl, bytesLeft);
        File.WriteAllBytes(pr, bytesRight);
        _arquivos.Add(pl);
        _arquivos.Add(pr);
        return (TextComparatorTests.Entrada(pl, bytesLeft.LongLength), TextComparatorTests.Entrada(pr, bytesRight.LongLength));
    }

    /// <summary>Entrada L0 mínima para comparação (não-placeholder).</summary>
    private static FileEntry Entrada(string path, long size, bool placeholder = false) => new()
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
