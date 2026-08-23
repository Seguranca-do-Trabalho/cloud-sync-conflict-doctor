namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// S11-1 (t_17f56008) — Path traversal e nomes hostis (T-01): canonização e contenção.
/// Fonte: docs/threat-model.md T-01/R2/R12; SPEC §7–§9 (níveis do scanner); ADR-0002
/// falha fechada; decisões D4/D5/D6 do card.
///
/// Primitivas sob teste:
/// - <see cref="PathCanonical"/>: canonização única + forma estendida \\?\, combinação
///   estrutural com re-canonização e contenção byte-a-byte (D4) — fronteira de tudo que
///   o produto move/escreve;
/// - <see cref="FileEntry.HasBidiControlChars"/>: marcador estrutural bidi (D5);
/// - integração: <see cref="QuarantineService.Move"/> e <see cref="QuarantineService.Restore"/>
///   executam íntegros sob nomes hostis e &gt; 260 chars, sem tocar nada fora da raiz.
///
/// SEG-01 (P0): árvore com "evil.txt." (trailing dot), nome RLO e homóglifo cirílico;
/// quarentena + restore com contenção validada; nenhum caminho fora da raiz tocado.
/// SEG-02 (P0): caminho &gt; 260 chars; move e restore íntegros via forma estendida.
/// SEG-03 (P2): nome RLO não engana saída JSON/GUI (marcador estrutural + escape JSON).
/// </summary>
[Trait("Category", "Security")]
public sealed class PathCanonicalSecurityTests : IDisposable
{
    private static readonly DateTimeOffset Congelado = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string raiz;
    private readonly QuarantineService servico = new();

    public PathCanonicalSecurityTests() =>
        raiz = Directory.CreateTempSubdirectory("cd-t01-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(raiz, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------
    // SEG-01 — Contenção byte-a-byte sob nomes hostis (T-01, P0)
    // ------------------------------------------------------------------

    [Fact]
    public void Security_PathTraversal_HostileName_ContainedInRoot()
    {
        // ---- fase A: a primitiva recusa escape por traversal -----------------
        // (área de teste própria, descartada no fim da fase — a isca da fase B é
        // criada DEPOIS, para não ser apagada pela limpeza desta fase)
        var areaFaseA = Directory.CreateTempSubdirectory("cd-fasea-");
        var raizFora = Directory.CreateTempSubdirectory("cd-fora-");
        try
        {
            // Destino fora da raiz: falha fechada.
            Assert.Throws<PathEscapeException>(
                () => PathCanonical.EnsureContained(
                    Path.Combine(raizFora.FullName, "x.txt"), raiz));

            // ".." canônico que sairia da raiz: falha fechada.
            Assert.Throws<PathEscapeException>(
                () => PathCanonical.EnsureContained(
                    Path.Combine(raiz, "..", "fora.txt"), raiz));

            // Combinação estrutural nasce dentro da raiz canônica...
            var destino = PathCanonical.Combine(raiz, "ConflictDoctor", "quarantine", "op-1", "payload");
            Assert.StartsWith(PathCanonical.CanonicalizeRoot(raiz), destino, StringComparison.Ordinal);

            // ...e segmento absoluto injetado NÃO é aceito como troca de raiz:
            // a combinação re-canonicaliza (desvio explícito) e a contenção reprova.
            var sequestrado = PathCanonical.Combine(raiz, areaFaseA.FullName);
            Assert.Throws<PathEscapeException>(() => PathCanonical.EnsureContained(sequestrado, raiz));
        }
        finally
        {
            areaFaseA.Delete(recursive: true);
        }

        // ---- fase B: árvore hostil real; quarentena + restore contidos -------
        var dirHostil = Directory.CreateDirectory(Path.Combine(raiz, "sub"));

        var pDot = CriarArquivoHostil(dirHostil.FullName, "evil.txt.");              // trailing dot
        var pRlo = CriarArquivoHostil(dirHostil.FullName, "fdp\u202Eexe.pdf");       // U+202E RTL override
        var pHomoglifo = CriarArquivoHostil(dirHostil.FullName, "\u0430rquivo.txt"); // 'а' cirílico

        // Isca fora da raiz: deve permanecer intocada durante TODA a operação.
        var isca = Path.Combine(raizFora.FullName, "isca.txt");
        File.WriteAllBytes(isca, [0xCA, 0xFE]);
        var iscaBytes = File.ReadAllBytes(isca);
        var iscaMtime = File.GetLastWriteTimeUtc(isca);

        var itens = new List<QuarantineItem>();
        foreach (var caminho in new[] { pDot, pRlo, pHomoglifo })
        {
            itens.Add(new QuarantineItem(
                Entrada(caminho),
                Reason: "nome hostil (T-01)",
                Rule: "R2"));
        }

        var resultado = servico.Move(itens, new QuarantinePlan(raiz, Congelado), CancellationToken.None);

        Assert.Equal("completed", resultado.Status);
        Assert.Equal(itens.Count, resultado.MovedPaths.Count);

        // Todo caminho produzido pela operação fica DENTRO da raiz, byte-a-byte.
        foreach (var movido in resultado.MovedPaths)
        {
            PathCanonical.EnsureContained(movido, raiz); // lança se escapar
        }

        // Restore devolve os originais byte-exatos (nomes idênticos em UTF-16)...
        var restaurado = servico.Restore(resultado.OperationId, raiz, CancellationToken.None);
        Assert.Equal(itens.Count, restaurado.RestoredPaths.Count);

        foreach (var original in new[] { pDot, pRlo, pHomoglifo })
        {
            var esperado = Path.GetFullPath(original);
            Assert.True(File.Exists(esperado), $"esperado de volta em disco: {esperado}");
            Assert.Contains(esperado, restaurado.RestoredPaths, StringComparer.Ordinal);
        }

        // ...a segunda passada de contenção permanece fechada... 
        foreach (var caminhoRestaurado in restaurado.RestoredPaths)
        {
            PathCanonical.EnsureContained(caminhoRestaurado, raiz);
        }

        // ...e nenhum caminho fora da raiz foi tocado: isca única, bytes e mtime intactos.
        Assert.Equal(new[] { isca }, Directory.GetFiles(raizFora.FullName, "*", SearchOption.AllDirectories));
        Assert.Equal(iscaBytes, File.ReadAllBytes(isca));
        Assert.Equal(iscaMtime, File.GetLastWriteTimeUtc(isca));

        // Nomes hostis preservados EXATAMENTE como o filesystem os deu (T-01 mitigação (c)):
        // nada foi "consertado" em silêncio — trailing dot, RLO e homóglifo voltam iguais.
        Assert.Equal(3, Directory.GetFiles(dirHostil.FullName).Length);

        // Manifesto: nomes byte-exatos, encoder estrito não alterou conteúdo decodificado.
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(resultado.ManifestPath));
        var caminhosNoManifesto = json.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("original_path").GetString())
            .ToArray();
        Assert.Equal(
            itens.Select(i => i.Entry.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            caminhosNoManifesto);
    }

    // ------------------------------------------------------------------
    // SEG-02 — > 260 chars com prefixo estendido, sem truncamento (P0)
    // ------------------------------------------------------------------

    [Fact]
    public void Security_LongPath_Over260Chars_ExtendedPrefixNoTruncation()
    {
        // Componentes de ~52 chars até o caminho final passar de 260 caracteres.
        const string componente = "componente-comprido-para-teste-de-caminho-longo-0123456789";
        var profundidade = Math.Max(1, (280 - raiz.Length) / (componente.Length + 1));
        var dirAtual = raiz;
        for (var i = 0; i < profundidade; i++)
        {
            dirAtual = Path.Combine(dirAtual, componente);
        }
        Directory.CreateDirectory(dirAtual);

        var nomeFinal = "relatorio-final-do-caso-com-nome-muito-comprido.dat";
        var caminhoLongo = Path.Combine(dirAtual, nomeFinal);

        var conteudo = "conteudo integral do arquivo longo"u8.ToArray();
        File.WriteAllBytes(PathCanonical.ToExtendedLength(caminhoLongo), conteudo);

        Assert.True(
            caminhoLongo.Length > 260,
            $"o teste exige caminho > 260 chars; obtido {caminhoLongo.Length}");

        var itens = new List<QuarantineItem> { new(Entrada(caminhoLongo), "caminho longo", "R2") };

        var resultado = servico.Move(itens, new QuarantinePlan(raiz, Congelado), CancellationToken.None);
        Assert.Equal("completed", resultado.Status);
        Assert.Single(resultado.MovedPaths);

        // Restore recria a árvore profunda sem truncar um único caractere.
        var restaurado = servico.Restore(resultado.OperationId, raiz, CancellationToken.None);
        Assert.Single(restaurado.RestoredPaths);

        var volta = Path.GetFullPath(caminhoLongo);
        Assert.True(File.Exists(volta), "payload deve voltar ao caminho > 260 intacto");
        Assert.Equal(conteudo, File.ReadAllBytes(volta));
        Assert.Equal(caminhoLongo.Length, restaurado.RestoredPaths[0].Length);
    }

    // ------------------------------------------------------------------
    // SEG-03 — Nome RLO não engana a saída JSON/GUI (T-01/R12, P2)
    // ------------------------------------------------------------------

    [Fact]
    public void Report_BidiControlChars_EscapedInJsonAndGui()
    {
        // Marcador estrutural (D5): o NOME nunca é mutado; a flag expõe o risco.
        var nomeRlo = "fdp\u202Eexe.pdf";

        Assert.False(PathCanonical.HasBidiControlChars("relatorio.pdf"));
        Assert.False(PathCanonical.HasBidiControlChars("foto v2.jpg"));
        Assert.False(PathCanonical.HasBidiControlChars(null));
        Assert.True(PathCanonical.HasBidiControlChars(nomeRlo));
        // Isolates/marcas também são controle bidi (lista fixa auditável).
        Assert.True(PathCanonical.HasBidiControlChars("a\u2066b\u2069.pdf"));
        Assert.True(PathCanonical.HasBidiControlChars("nota\u200Ffinal.docx"));

        var dir = Directory.CreateDirectory(Path.Combine(raiz, "rlo"));
        var caminho = CriarArquivoHostil(dir.FullName, nomeRlo);
        var entrada = Entrada(caminho);

        Assert.True(entrada.HasBidiControlChars);

        // JSON com o MESMO encoder estrito do relatório/manifesto (JavaScriptEncoder.Default):
        // U+202E sai escapado (\u202e) — o consumidor jamais vê bytes que reordenem
        // a renderização; após decodificar, o nome permanece byte-exato e a flag
        // estrutural acompanha (renderização cabe ao EPIC 10).
        var json = System.Text.Json.JsonSerializer.Serialize(
            new { name = entrada.Path, has_bidi_control_chars = entrada.HasBidiControlChars },
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            });

        Assert.DoesNotContain('\u202E', json);
        Assert.Contains("\\u202e", json, StringComparison.OrdinalIgnoreCase);

        var decodificado =
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
        Assert.Equal(entrada.Path, decodificado["name"].GetString());
        Assert.True(decodificado["has_bidi_control_chars"].GetBoolean());
    }

    // ------------------------------------------------------------------
    // infraestrutura
    // ------------------------------------------------------------------

    /// <summary>Snapshot L0 coerente com o contrato (FileEntry imutável).</summary>
    private FileEntry Entrada(string caminho)
    {
        var cheio = Path.GetFullPath(caminho);
        var info = new FileInfo(PathCanonical.ToExtendedLength(cheio));

        return new FileEntry
        {
            Path = cheio,
            Size = info.Length,
            MtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            Attributes = info.Attributes,
            VolumeId = "test-vol",
            FileId = $"fid-{Guid.NewGuid():N}",
        };
    }

    /// <summary>Cria arquivo cujo nome o Win32 cru recusaria (trailing dot/RLO/homóglifo), via forma estendida.</summary>
    private static string CriarArquivoHostil(string dir, string nome)
    {
        var destino = Path.Combine(dir, nome);
        File.WriteAllBytes(PathCanonical.ToExtendedLength(destino), [0x63, 0x64, 0x2D]);
        return destino;
    }
}
