namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// CD-01 (t_57b34881) — HSH-02, HSH-03 e HSH-04 do test-strategy (§3.6).
///
/// HSH-02 FullHash_ExecutedOnlyForPartialSurvivors:
///   arvore com pares mesmo-tamanho/conteudo-distinto (morrem no L2) ao lado
///   de par com colisao parcial garantida (cabeca+cauda fixas, miolo diferente);
///   CountingHasher prova que arquivo morto no L2 NUNCA recebe FullHash e que o
///   sobrevivente recebe exatamente 1 full hash por membro.
///
/// HSH-03 IdenticalContent_FullHashEqual_ClassifiedIdenticalDuplicate:
///   copias byte-identicas -> exatamente 1 IdenticalDuplicate, zero RealConflicts,
///   hash BLAKE3 hex 64 minusculo, ordem canonica em Files.
///
/// HSH-04 DifferentContent_SameNormalizedBase_ClassifiedRealConflict:
///   mesmo nome normalizado, conteudo diferente -> RealConflict cobrindo TODOS os
///   membros com hash individual; conflito real NUNCA aparece em IdenticalDuplicates
///   (Q9/Q10 do SPEC 58).
/// </summary>
public sealed class ConflictDetectionTests : IDisposable
{
    private const int Kib = 1024;
    private const int ArquivoGrande = 200 * Kib; // > 128 KiB: janelas parcial distintas
    private const int Janela = 64 * Kib;

    private readonly string _root;
    private readonly List<string> _tempFiles = new();

    public ConflictDetectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cd01-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ================================================================
    // HSH-02: FullHash executado SO para sobreviventes do Level 2
    // ================================================================

    [Fact]
    public void FullHash_ExecutadoApenasParaSobreviventesDoL2_SemChamadaEmMortos()
    {
        // Arvore com:
        //   - Par mesmo-tamanho/conteudo-distinto (parciais DIFERENTES -> morre no L2)
        //   - Par com colisao parcial (cabeca+cauda fixas, miolo diferente -> sobrevive ao L2)
        var conteudoDistintoA = ConteudoPadronizado(semente: 0xA1);
        var conteudoDistintoB = ConteudoPadronizado(semente: 0xB2); // mesmo tamanho, bytes diferentes
        var conteudoColisaoA = ConteudoComColisaoParcial(miolo: 0x11);
        var conteudoColisaoB = ConteudoComColisaoParcial(miolo: 0x22);

        var caminhoDistintoA = CriarArquivo("pasta/distintoA.bin", conteudoDistintoA);
        var caminhoDistintoB = CriarArquivo("pasta/distintoB.bin", conteudoDistintoB);
        var caminhoColisaoA = CriarArquivo("conflito/arquivo.xlsx", conteudoColisaoA);
        var caminhoColisaoB = CriarArquivo("conflito/arquivo-DESKTOP-ABC123.xlsx", conteudoColisaoB);

        var entries = new[]
        {
            Entrada(caminhoDistintoA),
            Entrada(caminhoDistintoB),
            Entrada(caminhoColisaoA),
            Entrada(caminhoColisaoB),
        };

        var counting = new CountingHasher();
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), counting);
        var result = pipeline.Run(_root);

        // ---- veredito esperado -----------------------------------------------
        // Par distinto: parcial diferente -> NAO survive L2 -> NAO entra nem em identical nem em conflicts
        // Par colisao:  parcial igual -> survive L2 -> full hash calculado -> resultado
        Assert.Empty(result.IdenticalDuplicates); // nao ha copia identica
        Assert.Single(result.RealConflicts);     // so o par de colisao
        Assert.Single(result.Groups);           // um grupo (base normalizada + size iguais)

        // ---- prova via CountingHasher ----------------------------------------
        var distintAEntrada = entries.Single(e => e.Path == caminhoDistintoA);
        var distintBEntrada = entries.Single(e => e.Path == caminhoDistintoB);
        var colisaoAEntrada = entries.Single(e => e.Path == caminhoColisaoA);
        var colisaoBEntrada = entries.Single(e => e.Path == caminhoColisaoB);

        // Arquivos mortos no L2 NUNCA receberam FullHash
        Assert.DoesNotContain(caminhoDistintoA, counting.FullHashPaths);
        Assert.DoesNotContain(caminhoDistintoB, counting.FullHashPaths);

        // Sobreviventes do L2 receberam exatamente 1 FullHash cada
        Assert.Contains(caminhoColisaoA, counting.FullHashPaths);
        Assert.Contains(caminhoColisaoB, counting.FullHashPaths);
        Assert.Equal(2, counting.FullHashPaths.Count);

        // Todos os membros do grupo sobrevivalente tem FullHash chamado uma vez
        Assert.Equal(1, counting.FullHashCalls[caminhoColisaoA]);
        Assert.Equal(1, counting.FullHashCalls[caminhoColisaoB]);
    }

    // ================================================================
    // HSH-03: IdenticalContent -> IdenticalDuplicate
    // ================================================================

    [Fact]
    public void IdenticalContent_FullHashEqual_ClassifiedAsIdenticalDuplicate()
    {
        var conteudo = new byte[50 * Kib];
        (new Random(77)).NextBytes(conteudo);

        // Mesma base normalizada: "a (1).txt" e "a (2).txt" normalizam para "a.txt"
        // => agrupam por base + tamanho; conteudo identico => IdenticalDuplicate.
        var c1 = CriarArquivo("dir/a (1).txt", conteudo);
        var c2 = CriarArquivo("dir/a (2).txt", conteudo);
        var c3 = CriarArquivo("outro/a (1).txt", conteudo); // terceiro copiado

        var entries = new[] { Entrada(c1), Entrada(c2), Entrada(c3) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // exactamente 1 IdenticalDuplicate
        Assert.Single(result.IdenticalDuplicates);
        Assert.Empty(result.RealConflicts);

        var dup = result.IdenticalDuplicates[0];

        // hash BLAKE3 hex 64 minusculo
        Assert.Matches(@"^[0-9a-f]{64}$", dup.Hash);

        // tamanho comum
        Assert.Equal(conteudo.Length, dup.SizeBytes);

        // 3 arquivos na lista, ordem canonica por bytes UTF-8
        // dir/a (1).txt < dir/a (2).txt < outro/a (1).txt (ordem de bytes)
        Assert.Equal(3, dup.Files.Count);
        Assert.Equal(c1, dup.Files[0].Path);
        Assert.Equal(c2, dup.Files[1].Path);
        Assert.Equal(c3, dup.Files[2].Path);
    }

    // ================================================================
    // HSH-04: DifferentContent_SameNormalizedBase -> RealConflict
    // ================================================================

    [Fact]
    public void DifferentContent_SameNormalizedBase_ClassifiedAsRealConflict()
    {
        // Cabeca+cauda fixas (colisao parcial garantida), miolos diferentes.
        // Tamanho > 128 KiB para que o parcial cubra apenas janelas e o full cubra tudo.
        var conteudoA = ConteudoComColisaoParcial(miolo: 0x11);
        var conteudoB = ConteudoComColisaoParcial(miolo: 0x22);

        // "orcamento-DESKTOP-ABC123.xlsx" normaliza para "orcamento.xlsx" (hostname 6 chars valido).
        var c1 = CriarArquivo("projeto/orcamento.xlsx", conteudoA);
        var c2 = CriarArquivo("projeto/orcamento-DESKTOP-ABC123.xlsx", conteudoB);

        var entries = new[] { Entrada(c1), Entrada(c2) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // exatamente 1 RealConflict
        Assert.Empty(result.IdenticalDuplicates);
        Assert.Single(result.RealConflicts);

        var conflito = result.RealConflicts[0];

        // nome normalizado identico
        Assert.Equal("orcamento.xlsx", conflito.NormalizedBaseName);
        Assert.Equal(conteudoA.Length, conflito.SizeBytes);

        // todos os membros cobertos com hash individual
        Assert.Equal(2, conflito.Files.Count);
        var memberA = conflito.Files.First(f => f.Path == c1);
        var memberB = conflito.Files.First(f => f.Path == c2);
        Assert.Matches(@"^[0-9a-f]{64}$", memberA.Hash);
        Assert.Matches(@"^[0-9a-f]{64}$", memberB.Hash);
        Assert.NotEqual(memberA.Hash, memberB.Hash); // hashes individuais distintos

        // conflito real NUNCA aparece em IdenticalDuplicates (Q9/Q10 do SPEC §58)
        foreach (var dup in result.IdenticalDuplicates)
        {
            Assert.DoesNotContain(c1, dup.Files.Select(f => f.Path));
            Assert.DoesNotContain(c2, dup.Files.Select(f => f.Path));
        }
    }

    // ================================================================
    // CD-02 (t_a77122c3) — Contrato de mútua exclusão do schema v1 §6.1/§6.2:
    // grupo classificado como IdenticalDuplicate NUNCA gera entrada em
    // RealConflicts e vice-versa; a classificação é POR GRUPO.
    // ================================================================

    // ----------------------------------------------------------------
    // Caso 1 — Grupo misto: 2 membros idênticos entre si + 1 divergente.
    // Schema §6.2: o elemento de real_conflicts cobre TODOS os membros,
    // incluindo o subconjunto internamente idêntico; §6.1: duplicata só se
    // forma de grupo cujos hashes completos são TODOS iguais. O subconjunto
    // interno NÃO vira IdenticalDuplicate — o consumidor reconstrói
    // subgrupos pelos hashes por arquivo.
    // ----------------------------------------------------------------
    [Fact]
    public void GrupoMisto_SubconjuntoInternoIdentico_NaoViraIdenticalDuplicate_ViraUmUnicoRealConflict()
    {
        // Três membros, mesma base normalizada ("relatorio.xlsx") e mesmo tamanho:
        //   A = relatorio.xlsx            (conteudo X)
        //   A = relatorio (1).xlsx        (conteudo X — identico a A)
        //   B = relatorio-DESKTOP-ABC123.xlsx (conteudo Y — divergente)
        // Cabeca+cauda fixas garantem colisao parcial (sobrevivem ao L2);
        // miolos distintos garantem full hashes distintos (X != Y).
        var conteudoX = ConteudoComColisaoParcial(miolo: 0x11);
        var conteudoY = ConteudoComColisaoParcial(miolo: 0x22);

        var caminhoA1 = CriarArquivo("financas/relatorio.xlsx", conteudoX);
        var caminhoA2 = CriarArquivo("financas/relatorio (1).xlsx", conteudoX);
        var caminhoB = CriarArquivo("backup/relatorio-DESKTOP-ABC123.xlsx", conteudoY);

        var entries = new[] { Entrada(caminhoA1), Entrada(caminhoA2), Entrada(caminhoB) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // UMA entrada única em RealConflicts cobrindo os 3 membros.
        Assert.Single(result.RealConflicts);
        var conflito = result.RealConflicts[0];
        Assert.Equal("relatorio.xlsx", conflito.NormalizedBaseName);
        Assert.Equal(conteudoX.Length, conflito.SizeBytes);
        Assert.Equal(3, conflito.Files.Count);
        Assert.Equal(
            new[] { caminhoA1, caminhoA2, caminhoB }.OrderBy(p => p, StringComparer.Ordinal),
            conflito.Files.Select(f => f.Path));

        // ZERO entradas em IdenticalDuplicates: o par interno identico NAO vira duplicata.
        Assert.Empty(result.IdenticalDuplicates);

        // Reconstrutibilidade do subconjunto pelos hashes por arquivo (schema §6.2):
        // exatamente dois valores de hash, o par identico compartilha um deles.
        var hashA1 = conflito.Files.First(f => f.Path == caminhoA1).Hash;
        var hashA2 = conflito.Files.First(f => f.Path == caminhoA2).Hash;
        var hashB = conflito.Files.First(f => f.Path == caminhoB).Hash;
        Assert.Equal(hashA1, hashA2);
        Assert.NotEqual(hashA1, hashB);
    }

    // ----------------------------------------------------------------
    // Caso 2 — Propriedade de varredura: para qualquer ScanResult, todo
    // normalized_base_name aparece em no máximo uma das duas listas
    // (schema §6.2, último parágrafo). Árvore composta que exerce todos
    // os desfechos de classificação no mesmo resultado: grupo misto
    // (RealConflict), trio idêntico (IdenticalDuplicate), par morto no L2
    // (presente só em Groups — trilha de auditoria §6.0) e arquivo único.
    // ----------------------------------------------------------------
    [Fact]
    public void Propriedade_BaseNormalizada_ApareceEmNoMaximoUmaDasDuasListas_ParaQualquerScanResult()
    {
        // Grupo misto -> RealConflict.
        var mistoX = ConteudoComColisaoParcial(miolo: 0x11);
        var mistoY = ConteudoComColisaoParcial(miolo: 0x22);
        var m1 = CriarArquivo("financas/relatorio.xlsx", mistoX);
        var m2 = CriarArquivo("financas/relatorio (1).xlsx", mistoX);
        var m3 = CriarArquivo("backup/relatorio-DESKTOP-ABC123.xlsx", mistoY);

        // Trio byte-idêntico -> IdenticalDuplicate.
        var identico = new byte[30 * Kib];
        for (var i = 0; i < identico.Length; i++)
        {
            identico[i] = (byte)(i % 253);
        }
        var d1 = CriarArquivo("docs/notas.txt", identico);
        var d2 = CriarArquivo("docs/notas (1).txt", identico);
        var d3 = CriarArquivo("docs/notas (2).txt", identico);

        // Par mesmo-tamanho/conteúdo-distinto -> morre no L2 (sem colisão parcial),
        // fica somente em Groups (auditoria), fora das duas listas finais.
        var mortoA = CriarArquivo("tmp/morto.bin", ConteudoPadronizado(semente: 0x01));
        var mortoB = CriarArquivo("tmp/morto-DESKTOP-ABC123.bin", ConteudoPadronizado(semente: 0x02));

        // Arquivo único -> nunca é candidato.
        var solo = CriarArquivo("raiz/solo.txt", [0x53, 0x4F, 0x4C, 0x4F]);

        var entries = new[] { Entrada(m1), Entrada(m2), Entrada(m3), Entrada(d1), Entrada(d2), Entrada(d3), Entrada(mortoA), Entrada(mortoB), Entrada(solo) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // Cenário montado como esperado: 3 grupos candidatos, 1 de cada veredito.
        Assert.Equal(3, result.Groups.Count);
        Assert.Single(result.IdenticalDuplicates);
        Assert.Single(result.RealConflicts);

        // PROPRIEDADE: interseção vazia entre os nomes normalizados das duas listas.
        var nomesEmConflitos = result.RealConflicts
            .Select(c => c.NormalizedBaseName)
            .ToHashSet(StringComparer.Ordinal);
        var nomesEmDuplicatas = result.IdenticalDuplicates
            .SelectMany(d => d.Files.Select(f => Grouping.NormalizeBaseName(Path.GetFileName(f.Path))))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(nomesEmConflitos.Intersect(nomesEmDuplicatas));

        // Reforço concreto: o grupo morto no L2 ("morto.bin") existe em Groups
        // (trilha de auditoria) mas aparece em NENHUMA das duas listas — zero
        // também respeita "no máximo uma".
        Assert.Contains(result.Groups, g => g.NormalizedBaseName == "morto.bin");
        Assert.DoesNotContain("morto.bin", nomesEmConflitos);
        Assert.DoesNotContain("morto.bin", nomesEmDuplicatas);

        // E cada lista contém exatamente o nome do seu próprio veredito.
        Assert.Equal(new[] { "relatorio.xlsx" }, nomesEmConflitos);
        Assert.Equal(new[] { "notas.txt" }, nomesEmDuplicatas);
    }

    // ----------------------------------------------------------------
    // Caso 3 — Único par do grupo sobrevive ao L2 (colisão parcial) mas
    // full hashes distintos: SOMENTE RealConflict, ZERO IdenticalDuplicates
    // (schema §6.2: ao menos dois hashes distintos após o L2 ⇒ conflito real).
    // ----------------------------------------------------------------
    [Fact]
    public void ParSobreviveAoL2_ComFullHashesDistintos_SoRealConflict_ZeroIdenticalDuplicates()
    {
        var conteudoA = ConteudoComColisaoParcial(miolo: 0x33);
        var conteudoB = ConteudoComColisaoParcial(miolo: 0x44);

        var caminhoA = CriarArquivo("contratos/termo.xlsx", conteudoA);
        var caminhoB = CriarArquivo("contratos/termo (1).xlsx", conteudoB);

        var entries = new[] { Entrada(caminhoA), Entrada(caminhoB) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // O grupo sobreviveu ao L2 e virou conflito — nada na lista de duplicatas.
        Assert.Single(result.RealConflicts);
        Assert.Empty(result.IdenticalDuplicates);

        var conflito = result.RealConflicts[0];
        Assert.Equal("termo.xlsx", conflito.NormalizedBaseName);
        Assert.Equal(2, conflito.Files.Count);

        var hashA = conflito.Files.First(f => f.Path == caminhoA).Hash;
        var hashB = conflito.Files.First(f => f.Path == caminhoB).Hash;
        Assert.Matches(@"^[0-9a-f]{64}$", hashA);
        Assert.Matches(@"^[0-9a-f]{64}$", hashB);
        Assert.NotEqual(hashA, hashB);
    }

    // ----------------------------------------------------------------
    // Caso 4 — Triplo byte-idêntico: UMA única IdenticalDuplicate com 3
    // Files e ZERO RealConflicts (schema §6.1: um elemento por classe de
    // equivalência por conteúdo pleno, com 2 ou mais arquivos).
    // ----------------------------------------------------------------
    [Fact]
    public void TriploByteIdentico_UnicaIdenticalDuplicate_ComTresArquivos_ZeroRealConflicts()
    {
        var conteudo = new byte[12 * Kib];
        for (var i = 0; i < conteudo.Length; i++)
        {
            conteudo[i] = (byte)(0xC3 ^ (i % 31));
        }

        // Bases que normalizam todas para "config.ini".
        var c1 = CriarArquivo("app/config.ini", conteudo);
        var c2 = CriarArquivo("app/config (1).ini", conteudo);
        var c3 = CriarArquivo("bak/config-DESKTOP-ZZZ999.ini", conteudo);

        var entries = new[] { Entrada(c1), Entrada(c2), Entrada(c3) };
        var pipeline = new ScanPipeline(new FakeFileEnumerator(entries), new Blake3Hasher());
        var result = pipeline.Run(_root);

        // UMA entrada única, três arquivos, nenhum conflito real.
        Assert.Single(result.IdenticalDuplicates);
        Assert.Empty(result.RealConflicts);

        var dup = result.IdenticalDuplicates[0];
        Assert.Matches(@"^[0-9a-f]{64}$", dup.Hash);
        Assert.Equal(conteudo.Length, dup.SizeBytes);
        Assert.Equal(3, dup.Files.Count);
        Assert.Equal(
            new[] { c1, c2, c3 }.OrderBy(p => p, StringComparer.Ordinal),
            dup.Files.Select(f => f.Path));
    }

    // ---- helpers ----------------------------------------------------------

    private string CriarArquivo(string relativo, byte[] conteudo)
    {
        var caminho = Path.Combine(_root, relativo.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllBytes(caminho, conteudo);
        _tempFiles.Add(caminho);
        return caminho;
    }

    private static FileEntry Entrada(string caminho) => new()
    {
        Path = caminho,
        Size = new FileInfo(caminho).Length,
        MtimeUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        VolumeId = "cd01-vol",
        FileId = caminho,
    };

    private static byte[] ConteudoPadronizado(byte semente)
    {
        var bytes = new byte[ArquivoGrande];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(semente + (i % 251));
        }
        return bytes;
    }

    /// <summary>Cabeca e cauda fixas (colisao parcial garantida); miolo varia por semente.</summary>
    private static byte[] ConteudoComColisaoParcial(byte miolo)
    {
        var bytes = new byte[ArquivoGrande];
        for (var i = 0; i < Janela; i++)
        {
            bytes[i] = (byte)(0xAA + (i % 13));
        }
        for (var i = ArquivoGrande - Janela; i < ArquivoGrande; i++)
        {
            bytes[i] = (byte)(0xBB + (i % 17));
        }
        for (var i = Janela; i < ArquivoGrande - Janela; i++)
        {
            bytes[i] = miolo;
        }
        return bytes;
    }

    private sealed class FakeFileEnumerator : IFileEnumerator
    {
        private readonly IReadOnlyList<FileEntry> _ordem;
        public FakeFileEnumerator(IReadOnlyList<FileEntry> ordem) => _ordem = ordem;
        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) =>
            new(_ordem, Array.Empty<ScanError>(), new ScanTelemetry());
    }

    /// <summary>
    /// Hasher espião que delega para um hasher real mas registra todas as chamadas.
    /// </summary>
    private sealed class CountingHasher : IHasher
    {
        private readonly Blake3Hasher _real = new();

        public List<string> FullHashPaths { get; } = new();
        public Dictionary<string, int> FullHashCalls { get; } = new();

        public string PartialHash(FileEntry entry, CancellationToken ct = default) =>
            _real.PartialHash(entry, ct);

        public string FullHash(FileEntry entry, CancellationToken ct = default)
        {
            FullHashPaths.Add(entry.Path);
            if (!FullHashCalls.TryAdd(entry.Path, 1))
            {
                FullHashCalls[entry.Path]++;
            }
            return _real.FullHash(entry, ct);
        }
    }
}
