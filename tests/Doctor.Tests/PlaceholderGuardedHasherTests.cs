namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — PLH-01 e teste negativo de mutação (docs/test-strategy.md §3.2;
/// SPEC §6/§21; ADR-0005 item 6; threat-model T-03).
/// Espiões provam: nenhuma função de hash chamada sobre placeholder, nenhum stream
/// aberto sobre placeholder — logo zero bytes lidos.
/// </summary>
public class PlaceholderGuardedHasherTests
{
    private static FileEntry Entrada(
        string path,
        long size = 10,
        FileAttributes? attrs = null,
        bool isPlaceholder = false,
        PlaceholderKind? kind = null)
        => new()
        {
            Path = path,
            Size = size,
            MtimeUtc = DateTimeOffset.UnixEpoch,
            Attributes = attrs ?? FileAttributes.Normal,
            VolumeId = "vol-test",
            FileId = "1",
            IsPlaceholder = isPlaceholder,
            PlaceholderKind = kind,
        };

    public static TheoryData<FileAttributes, PlaceholderKind> MotivosDoSpec21 => new()
    {
        { FileAttributes.Offline, PlaceholderKind.Offline },
        { (FileAttributes)0x00040000, PlaceholderKind.RecallOnOpen },
        { (FileAttributes)0x00400000, PlaceholderKind.RecallOnDataAccess },
        { FileAttributes.ReparsePoint, PlaceholderKind.ReparsePoint },
    };

    [Theory]
    [MemberData(nameof(MotivosDoSpec21))]
    public void Gate_Placeholder_NaoChamaHash_NemAbreStream_NemLeBytes(
        FileAttributes attrs, PlaceholderKind kind)
    {
        // PLH-01: cada motivo do §21, nas duas operações do contrato IHasher.
        foreach (var usarFull in new[] { false, true })
        {
            var fonte = new CountingStreamSource();
            var interno = CountingHasher.Using(fonte);
            var protegido = new PlaceholderGuardedHasher(interno);
            fonte.Register("tree/ph.bin", new byte[2048]); // se abrir, o espião registra

            var entry = Entrada("tree/ph.bin", size: 2048, attrs: attrs, isPlaceholder: true, kind: kind);

            var ex = usarFull
                ? Assert.Throws<PlaceholderReadException>(() => protegido.FullHash(entry))
                : Assert.Throws<PlaceholderReadException>(() => protegido.PartialHash(entry));

            Assert.Equal(entry.Path, ex.EntryPath);
            Assert.Equal(0, interno.Calls.GetValueOrDefault(entry.Path)); // nenhuma função de hash (SPEC §21)
            Assert.Equal(0, fonte.OpenCount(entry.Path));                 // nenhum stream aberto
            Assert.Equal(0, fonte.TotalBytes);                            // logo, zero bytes lidos
        }
    }

    [Fact]
    public void Gate_EntradaNaoMarcada_ComBitsDePlaceholder_TambemEBloqueada()
    {
        // Defesa em profundidade (threat-model T-03): marcação ausente/stale do Level 0
        // não passa — o gate reclassifica pelos atributos crus ANTES de qualquer abertura.
        var fonte = new CountingStreamSource();
        var interno = CountingHasher.Using(fonte);
        var protegido = new PlaceholderGuardedHasher(interno);
        fonte.Register("tree/offline.bin", new byte[512]);

        var entry = Entrada("tree/offline.bin", size: 512, attrs: FileAttributes.Offline, isPlaceholder: false);

        var exP = Assert.Throws<PlaceholderReadException>(() => protegido.PartialHash(entry));
        var exF = Assert.Throws<PlaceholderReadException>(() => protegido.FullHash(entry));
        Assert.Equal(0, interno.Calls.GetValueOrDefault(entry.Path));
        Assert.Equal(0, fonte.OpenCount(entry.Path));
        Assert.Null(exP.InnerException);
    }

    [Fact]
    public void Gate_ArquivoNormal_DelegaAoInterno()
    {
        var fonte = new CountingStreamSource();
        fonte.Register("tree/a.bin", [1, 2, 3, 4]);
        var interno = CountingHasher.Using(fonte);
        var protegido = new PlaceholderGuardedHasher(interno);
        var entry = Entrada("tree/a.bin", size: 4);

        Assert.Equal("fake-hash", protegido.PartialHash(entry));
        Assert.Equal("fake-hash", protegido.FullHash(entry));
        Assert.Equal(2, interno.Calls["tree/a.bin"]);
        Assert.Equal(2, fonte.OpenCount("tree/a.bin"));
        Assert.Equal(8, fonte.BytesRead("tree/a.bin")); // 4 partial + 4 full
    }

    [Fact]
    public void Mutacao_GateRemovido_EhDetectadaPelosEspioes()
    {
        // Teste negativo exigido pelo card: uma mutação que faça o gate abrir o
        // placeholder DEVE falhar. Prova que os espiões enxergam a violação: sem o
        // decorator, o hasher "consegue" abrir — e é exatamente isso que fica vermelho.
        var fonte = new CountingStreamSource();
        fonte.Register("tree/p.offline", new byte[4096]);
        var mutante = CountingHasher.Using(fonte); // sem PlaceholderGuardedHasher
        var entry = Entrada("tree/p.offline", size: 4096, attrs: FileAttributes.Offline, isPlaceholder: true);

        _ = mutante.PartialHash(entry); // mutação simulada: bypass do gate

        Assert.True(mutante.Calls[entry.Path] > 0);          // hash chamado sobre placeholder...
        Assert.True(fonte.OpenCount(entry.Path) > 0);        // ...stream aberto...
        Assert.True(fonte.BytesRead(entry.Path) > 0);        // ...bytes lidos — violação exposta
    }
}
