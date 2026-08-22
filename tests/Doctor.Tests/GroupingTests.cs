using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T08 (t_6c210834) — Agrupamento Level 1 (SPEC §7; ADR-0004 §5; test-strategy §3.6).
/// IDs cobertos: GRP-01 (lista fixa com positivo e negativo por padrão),
/// GRP-02 (sufixo desconhecido não normaliza; versão registrada),
/// GRP-03 (só mesmo nome normalizado + size agrupa) e prova de determinismo
/// do recorte L1: ordem de entrada não muda a saída (regra estrutural ADR-0004 §1,
/// recorte de DET-01/DET-02 limitado ao agrupamento serial deste card).
/// </summary>
public class GroupingTests
{
    private static FileEntry Entry(string path, long size) => new()
    {
        Path = path,
        Size = size,
        MtimeUtc = DateTimeOffset.UnixEpoch,
        Attributes = FileAttributes.Normal,
        VolumeId = "v",
        FileId = path,
    };

    // ---- GRP-01: cada padrão da lista fixa com caso positivo -----------------

    [Theory]
    [InlineData("relatorio (conflicted copy).xlsx", "relatorio.xlsx")]
    [InlineData("orcamento-DESKTOP-ABC123.xlsx", "orcamento.xlsx")]
    [InlineData("foto (1).jpg", "foto.jpg")]
    [InlineData("foto (2).jpg", "foto.jpg")]
    [InlineData("backup.txt~", "backup.txt")]
    [InlineData("~$curriculo.docx", "curriculo.docx")]
    [InlineData("backup.sb-a3f19c.txt", "backup.txt")]
    public void NormalizeBaseName_PadraoConhecido_ReduzAoNomeBase(string nome, string esperado)
    {
        Assert.Equal(esperado, Grouping.NormalizeBaseName(nome));
    }

    [Fact]
    public void NormalizeBaseName_PadroesCompostos_FixpointDentroDoLimite()
    {
        // Composição real de sincronização: lockfile Office + hostname + cópia numerada.
        Assert.Equal(
            "Relatorio.xlsx",
            Grouping.NormalizeBaseName("~$Relatorio-DESKTOP-ABC123 (conflicted copy).xlsx"));
    }

    // ---- GRP-01: caso negativo por padrão (nome sem o padrão fica intacto) ----

    [Theory]
    [InlineData("relatorio.xlsx")]                       // sem " (conflicted copy)"
    [InlineData("relatorio (conflicted copy)v2.xlsx")]   // padrão não está junto à extensão
    [InlineData("orcamento.xlsx")]                       // sem -DESKTOP-
    [InlineData("orcamento-SERVIDOR-ABC123.xlsx")]       // marcador é "-DESKTOP-", outro host não casa
    [InlineData("foto.jpg")]                             // sem " (N)"
    [InlineData("backup.txt")]                           // sem ~ final
    [InlineData("curriculo.docx")]                       // sem prefixo ~$
    public void NormalizeBaseName_SemPadrao_NomeIntacto(string nome)
    {
        Assert.Equal(nome, Grouping.NormalizeBaseName(nome));
    }

    // ---- GRP-02: sufixo desconhecido NÃO normaliza; versão registrada --------

    [Fact]
    public void NormalizeBaseName_SufixoForaDaLista_NaoNormaliza_EVersaoEhUm()
    {
        // " (3)" NÃO está na lista fixa (só " (1)" e " (2)") — engolir seria heurística
        // infinita (SPEC §7; risco R6). O mesmo vale para sufixos arbitrários.
        Assert.Equal("documento (3).pdf", Grouping.NormalizeBaseName("documento (3).pdf"));
        Assert.Equal("arquivo.bak2", Grouping.NormalizeBaseName("arquivo.bak2"));
        Assert.Equal("backup.sb-ZZ9.txt", Grouping.NormalizeBaseName("backup.sb-ZZ9.txt"));

        // Versão da lista fixa registrada para o relatório (normalizer_version).
        Assert.Equal(1, Grouping.NormalizerVersion);
    }

    // ---- GRP-03: só mesmo nome normalizado + size agrupa ---------------------

    [Fact]
    public void Group_MesmoNomeEMesmoSize_Agrupa_OrdenadoPorCaminho()
    {
        var groups = Grouping.Group(new[]
        {
            Entry("/r/docs/foto (1).jpg", 100),
            Entry("/r/fotos/foto.jpg", 100),
            Entry("/r/a/foto (conflicted copy).jpg", 100),
        });

        var group = Assert.Single(groups);
        Assert.Equal("foto.jpg", group.NormalizedBaseName);
        Assert.Equal(100, group.SizeBytes);
        Assert.Equal(
            new[] { "/r/a/foto (conflicted copy).jpg", "/r/docs/foto (1).jpg", "/r/fotos/foto.jpg" },
            group.Members.Select(m => m.Path).ToArray());
    }

    [Fact]
    public void Group_TamanhosDiferentesOuBasesDiferentes_NuncaAgrupam()
    {
        var groups = Grouping.Group(new[]
        {
            Entry("/r/foto.jpg", 100),
            Entry("/r/foto (1).jpg", 200),   // mesma base, tamanho diferente
            Entry("/r/video.mp4", 100),      // mesmo tamanho, base diferente
            Entry("/r/sozinho.txt", 5),      // grupo unitário não vira candidato
        });

        Assert.All(groups, g => Assert.True(g.Members.Count >= 2));
        Assert.Empty(groups);
    }

    // ---- Determinismo no recorte L1 (ADR-0004 §1): entrada em ordens distintas
    // ---- produz saída idêntica; grupos ordenados por (base bytes, size). ------

    [Fact]
    public void Group_OrdemDeEntradaDistinta_SaidaIdentica_EOrdenacaoCanonica()
    {
        var entradas = new[]
        {
            Entry("/r/z/notas.txt", 10),
            Entry("/r/z/notas (1).txt", 10),
            Entry("/r/a/orcamento.xlsx", 30),
            Entry("/r/m/orcamento (conflicted copy).xlsx", 30),
            Entry("/r/m/orcamento.xlsx", 70),          // mesma base, size diferente
            Entry("/r/A/notas.txt", 10),               // "A" < "z" em bytes (Ordinal)
        };

        var ordem1 = Grouping.Group(entradas);
        var ordem2 = Grouping.Group(entradas.Reverse());

        static List<string> Projeta(IReadOnlyList<ConflictGroup> groups) => groups
            .Select(g => $"{g.NormalizedBaseName}|{g.SizeBytes}|{string.Join(";", g.Members.Select(m => m.Path))}")
            .ToList();

        Assert.Equal(Projeta(ordem1), Projeta(ordem2));

        // Ordem canônica dos grupos: base em bytes, depois size ascendente.
        // Grupo ("orcamento.xlsx", 70) é UNITÁRIO e por isso NÃO aparece (§6.0).
        Assert.Equal(
            new[] { ("notas.txt", 10L), ("orcamento.xlsx", 30L) },
            ordem1.Select(g => (g.NormalizedBaseName, g.SizeBytes)).ToArray());
        Assert.Equal(
            new[] { "/r/A/notas.txt", "/r/z/notas (1).txt", "/r/z/notas.txt" },
            ordem1[0].Members.Select(m => m.Path).ToArray());
    }
}
