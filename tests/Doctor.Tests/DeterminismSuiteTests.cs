namespace Doctor.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Doctor.Core;

/// <summary>
/// T21 (t_1b602b7b) — Suíte de determinismo completo DET-01..DET-06
/// (docs/test-strategy.md §3.1; SPEC §20; ADR-0003/0004; GATE 2).
///
/// Árvore padrão desta suíte (todas as famílias usam a MESMA composição exigida
/// pelo card): 50 arquivos = 25 pareados idênticos (11 pares + 1 trio),
/// 10 conflitos reais (5 grupos × 2 cópias com mesmas janelas parciais e miolos
/// distintos) e 15 únicos. Conteúdos, tamanhos, nomes e mtimes derivados SOMENTE
/// de índices — zero aleatoriedade fora das permutas declaradamente semeadas.
///
/// Camadas de comparação byte-a-byte nos testes de integração:
/// 1. Serialização canônica do ScanResult (padrão T12); e
/// 2. Relatório schema v1 completo via ReportWriterJson com timestamps
///    congelados (condição §2.3) e caminhos relativos à raiz (condição §2.4).
///
/// Desvios documentados em relação à tabela §3.1:
/// - DET-03: o produto NÃO tem pool de hash paralelo — a decisão é serial por
///   construção (ADR-0004 regras 1-2; ScanPipeline). A prova equivalente exigida
///   pelo gate é que a ORDEM DE TÉRMINO DE THREADS não altera a saída: N scans
///   concorrentes (ThreadPool, entradas embaralhadas distintas) devem produzir
///   bytes idênticos ao scan serial. Qualquer estado compartilhado mutável
///   quebraria este teste.
/// - DET-06: o campo generated_from.root_path é absoluto por schema e varia por
///   máquina; a máscara "&lt;RAIZ&gt;" substitui APENAS esse valor antes da
///   comparação. Todo o restante do relatório é comparado byte a byte contra o
///   golden commitado. Regeneração: env DOCTOR_REGEN_GOLDENS=1
///   (dotnet test --filter FullyQualifiedName~DeterminismSuiteTests).
/// </summary>
[Trait("Category", "Determinism")]
public sealed class DeterminismSuiteTests : IDisposable
{
    private const int Kib = 1024;

    /// <summary>Tamanho grande (&gt; 128 KiB): parcial = janelas início+fim (ADR-0005 §3).</summary>
    private const int ArquivoGrande = 200 * Kib;
    private const int Janela = 64 * Kib;

    private static readonly DateTimeOffset CongeladoInicio = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CongeladoFim = new(2026, 1, 1, 12, 0, 1, TimeSpan.Zero);

    /// <summary>DET-02: 10 seeds FIXAS declaradas em código (§3.1 — nenhuma aleatoriedade).</summary>
    private static readonly int[] SementesFixas =
    [
        1, 7, 42, 255, 1024, 40961, 123456, 987654321, 1999999999, 2147483646,
    ];

    private readonly string _root;

    public DeterminismSuiteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"t21-det-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // limpeza best-effort: tmp do sistema operacional recolhe depois
        }
    }

    // =====================================================================================
    // DET-01 — três ordens de enumeração ⇒ relatórios byte-idênticos
    // =====================================================================================

    [Fact]
    public void Scan_ThreeEnumerationOrders_ReportsByteIdentical()
    {
        var arvore = CriarArvorePadrao();
        var entradas = arvore.Entradas; // ordem de criação = ordem física "real"

        var ordens = new (string Rotulo, FileEntry[] Ordem)[]
        {
            ("direta", entradas.ToArray()),
            ("reversa", entradas.Reverse().ToArray()),
            ("embaralhada-seed-42", Shuffle(entradas, seed: 42)),
        };

        // Sanidade da prova: as três ordens físicas precisam ser DIFERENTES entre si,
        // senão o teste seria vacínio.
        Assert.NotEqual(ordens[0].Ordem.Select(e => e.Path), ordens[1].Ordem.Select(e => e.Path));
        Assert.NotEqual(ordens[0].Ordem.Select(e => e.Path), ordens[2].Ordem.Select(e => e.Path));

        var hasher = new Blake3Hasher();

        var referencia = ExecutarScanDuasCamadas(arvore.Raiz, ordens[0].Ordem, hasher);

        foreach (var (_, ordem) in ordens.Skip(1))
        {
            var candidato = ExecutarScanDuasCamadas(arvore.Raiz, ordem, hasher);

            AssertBytesIdenticos(
                referencia.ResultadoCanonico,
                candidato.ResultadoCanonico,
                $"DET-01 ScanResult: ordem '{ordens[0].Rotulo}' vs demais");
            AssertBytesIdenticos(
                referencia.RelatorioV1,
                candidato.RelatorioV1,
                $"DET-01 relatorio-v1: ordem '{ordens[0].Rotulo}' vs demais");
        }

        // ---- correção do veredito (não só determinismo) ---------------------------
        var resultado = RodarPipeline(ordens[0].Ordem, hasher).Run(arvore.Raiz);

        // Diagnóstico T21: composição observada × esperada (falha com contagem real).
        Assert.True(
            resultado.IdenticalDuplicates.Count == 12 && resultado.RealConflicts.Count == 5,
            "composicao inesperada: identical=" + resultado.IdenticalDuplicates.Count +
            " conflicts=" + resultado.RealConflicts.Count +
            " groups=" + resultado.Groups.Count +
            " | dups: " + string.Join(";", resultado.IdenticalDuplicates.Select(d => d.Files.Count + "x@" + d.SizeBytes)) +
            " | conflitos: " + string.Join(";", resultado.RealConflicts.Select(c => c.NormalizedBaseName)));

        // Candidatos: 11 pares + 1 trio + 5 conflitos = 17 grupos; vereditos:
        // 12 duplicatas idênticas e 5 conflitos reais; únicos morrem no L1.
        Assert.Equal(17, resultado.Groups.Count);
        Assert.Equal(12, resultado.IdenticalDuplicates.Count);
        Assert.Equal(5, resultado.RealConflicts.Count);
        Assert.Equal(25, resultado.IdenticalDuplicates.Sum(d => d.Files.Count));
        Assert.Equal(10, resultado.RealConflicts.Sum(c => c.Files.Count));

        // Ordem canônica dentro das listas: primeiro grupo é o par em backup/
        // ('b' 0x62 precede 'd' 0x64 de dups/ e 't' 0x74 de trio/).
        var primeiraDup = resultado.IdenticalDuplicates[0];
        Assert.Equal(2, primeiraDup.Files.Count);
        Assert.EndsWith("backup/p00/foto-p00.jpg", primeiraDup.Files[0].Path, StringComparison.Ordinal);
        Assert.Equal(64, primeiraDup.Hash.Length); // BLAKE3 hex minúscula (ADR-0005 §1)

        // Primeiro conflito: membro em conflicts/mirror/ precede conflicts/
        // ('m' 0x6D < 'o' 0x6F).
        var primeiroConflito = resultado.RealConflicts[0];
        Assert.Equal("orc-k00.bin", primeiroConflito.NormalizedBaseName);
        Assert.Contains("conflicts/mirror/", primeiroConflito.Files[0].Path, StringComparison.Ordinal);
        Assert.NotEqual(primeiroConflito.Files[0].Hash, primeiroConflito.Files[1].Hash);

        // Invariante de segurança ecoada na telemetria projetada (SPEC §21).
        Assert.Equal(0, referencia.Telemetria.PlaceholderBytesRead);
    }

    // =====================================================================================
    // DET-02 — dez seeds fixas de embaralhamento ⇒ todas idênticas à ordem direta
    // =====================================================================================

    [Fact]
    public void Scan_TenFixedShuffleSeeds_AllReportsByteIdentical()
    {
        var arvore = CriarArvorePadrao();
        var hasher = new Blake3Hasher();

        var referencia = ExecutarScanDuasCamadas(arvore.Raiz, arvore.Entradas.ToArray(), hasher);

        foreach (var seed in SementesFixas)
        {
            var permutada = Shuffle(arvore.Entradas, seed);

            // Sanidade: a permutação com esta seed NÃO pode coincidir com a direta.
            Assert.NotEqual(arvore.Entradas.Select(e => e.Path), permutada.Select(e => e.Path));

            var candidato = ExecutarScanDuasCamadas(arvore.Raiz, permutada, hasher);

            AssertBytesIdenticos(
                referencia.ResultadoCanonico,
                candidato.ResultadoCanonico,
                $"DET-02 ScanResult: seed {seed}");
            AssertBytesIdenticos(
                referencia.RelatorioV1,
                candidato.RelatorioV1,
                $"DET-02 relatorio-v1: seed {seed}");
        }
    }

    // =====================================================================================
    // DET-03 — ordem de término de threads não altera a saída
    // =====================================================================================

    /// <summary>
    /// Prova §11/§20 adaptada ao produto real (ver doc da classe): o pipeline decide
    /// em série sobre coleções canônicas; o que o gate exige é que a concorrência
    /// externa (N scans simultâneos terminando em ordem arbitrária, cada um com uma
    /// permutação física diferente) não produza NEM UM BYTE diferente do scan serial
    /// de referência (DET-01). Estado estático mutável ou decisão por ordem de chegada
    /// falharia aqui de forma intermitente.
    /// </summary>
    [Fact]
    public void ParallelHashing_ThreadCompletionOrder_ReportBytesIdentical()
    {
        var arvore = CriarArvorePadrao();
        var hasher = new Blake3Hasher();

        var serial = ExecutarScanDuasCamadas(arvore.Raiz, arvore.Entradas.ToArray(), hasher);

        const int trabalhadores = 12;
        var resultados = new byte[trabalhadores][];

        Parallel.For(0, trabalhadores, w =>
        {
            var permutada = Shuffle(arvore.Entradas, seed: (w * 13) + 5);
            resultados[w] = ExecutarScanDuasCamadas(arvore.Raiz, permutada, hasher).RelatorioV1;
        });

        for (var w = 0; w < trabalhadores; w++)
        {
            AssertBytesIdenticos(
                serial.RelatorioV1,
                resultados[w],
                $"DET-03 relatorio-v1: trabalhador {w} (terminou na posição {w}, ordem de conclusão arbitrária)");
        }
    }

    // =====================================================================================
    // DET-04 — ordem canônica = bytes UTF-8, imune à cultura
    // =====================================================================================

    /// <summary>
    /// Unit (§3.1): PathOrder.Comparer sobre o universo de 50 entradas desta suíte
    /// (incluindo os cinco nomes-armadilha Zebra/apple/Apple/zebra/Árvore) com
    /// CurrentCulture forçada a tr-TR e pt-BR. Oráculo independente: comparação
    /// lexicográfica dos bytes UTF-8 implementada AQUI, fora do produto. A ordem
    /// resultante deve ser IGUAL ao oráculo nas duas culturas, e a posição relativa
    /// dos cinco nomes-armadilha é a esperada explícita (maiúscula &lt; minúscula &lt;
    /// acentuada: 0x41 &lt; 0x5A &lt; 0x61 &lt; 0x7A &lt; 0xC3).
    /// </summary>
    [Fact]
    public void PathOrdering_UsesUtf8ByteOrder_RegardlessOfCulture()
    {
        var universo = UniversoEmMemoria(); // 50 entradas, sem disco (unit)

        var esperado = universo
            .OrderBy(e => e.Path, Comparer<string>.Create(ComparaPorBytesUtf8))
            .Select(e => e.Path)
            .ToArray();

        // Posição relativa EXPLÍCITA dos cinco nomes-armadilha (tabela §3.1).
        var armadilhas = new[] { "Apple.txt", "Zebra.txt", "apple.txt", "zebra.txt", "Árvore.txt" };
        var posicoes = armadilhas
            .Select(nome => Array.FindIndex(esperado, p => p.EndsWith("/" + nome, StringComparison.Ordinal)))
            .ToArray();
        Assert.All(posicoes, p => Assert.True(p >= 0, $"nome-armadilha ausente do universo: índice {p}"));
        Assert.True(posicoes.SequenceEqual(posicoes.OrderBy(p => p).ToArray()),
            "ordem UTF-8 esperada entre armadilhas violada no oráculo");

        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var cultura in new[] { "tr-TR", "pt-BR" })
            {
                var ci = new CultureInfo(cultura);
                CultureInfo.CurrentCulture = ci;
                CultureInfo.CurrentUICulture = ci;

                // Entrada embaralhada (seed 77) em cópia FRESCA por cultura.
                var entrada = Shuffle(universo, seed: 77);
                var obtido = entrada
                    .OrderBy(e => e, PathOrder.Comparer)
                    .Select(e => e.Path)
                    .ToArray();

                Assert.Equal(
                    esperado,
                    obtido,
                    StringComparer.Ordinal);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    // =====================================================================================
    // DET-05 — desempate mtime → size → path; nunca first-seen
    // =====================================================================================

    /// <summary>
    /// Unit (§3.1): resolvedor (§17) com empates construídos. Cada cenário é executado
    /// com a entrada em ordem direta, reversa e embaralhada (seeds fixas); vencedor e
    /// sequência de sacrificados devem ser IDÊNTICOS em todas as apresentações. Em todo
    /// cenário a ordem "direta" coloca um PERDEDOR na primeira posição — um resolvedor
    /// first-seen pegaria o vencedor errado.
    ///
    /// Nota de contrato: o agrupador (Level 1) só produz grupos de size uniforme; a
    /// cadeia mtime→size→path existe como defesa em profundidade no resolvedor e é
    /// exercida aqui com grupos construídos diretamente (ConflictGroup é contrato
    /// público — docs/test-strategy.md §3.1 DET-05 pede exatamente esses empates).
    /// </summary>
    [Fact]
    public void TieBreak_MtimeThenSizeThenPath_NeverFirstSeen()
    {
        var ordens = new[] { 0, -1, 11, 23 }; // 0=direta, -1=reversa, demais=shuffle

        // ---- Cenário A: mtime igual → decide SIZE (keep-newest) -------------------
        // Primeiro elemento da apresentação direta é o MENOR arquivo (perdedor óbvio).
        var grupoA = GrupoComMembros(
            mtimeIndice: 10,
            ("/g/a-menor.docx", 100L),
            ("/g/c-medio.docx", 200L),
            ("/g/b-maior.docx", 300L));
        var vencedorA = "/g/b-maior.docx";
        VerificarEstabilidade(grupoA, new KeepNewest(), vencedorA, ordens, "A keep-newest");

        // ---- Cenário B: keep-largest ignora mtime mais recente de terceiros -------
        var grupoB = GrupoComMembros(
            mtimeIndice: 20,
            ("/h/recem-modificado.docx", 111L),
            ("/h/z-pequeno.docx", 999L),
            ("/h/a-medio.docx", 500L));
        var vencedorB = "/h/z-pequeno.docx"; // maior size vence mesmo com mtime menos recente
        VerificarEstabilidade(grupoB, new KeepLargest(), vencedorB, ordens, "B keep-largest");

        // ---- Cenário C: mtime + size iguais → decide PATH (par real do universo) --
        // Réplica do par foto-p00 (mesmo mtime, mesmo size): vence o menor caminho.
        var grupoC = GrupoComMembros(
            mtimeIndice: 30,
            ("/arvore/backup/p00/foto-p00.jpg", 64L * Kib),
            ("/arvore/dups/p00/foto-p00.jpg", 64L * Kib));
        var vencedorC = "/arvore/backup/p00/foto-p00.jpg"; // 'b' < 'd'
        VerificarEstabilidade(grupoC, new KeepNewest(), vencedorC, ordens, "C empate absoluto");

        // ---- Cenário D: keep-machine entre representantes da MESMA máquina --------
        var grupoD = GrupoComMembros(
            mtimeIndice: 40,
            ("/i/doc-DESKTOP-HOSTZZZZZ.xlsx", 400L),
            ("/i/doc-DESKTOP-HOSTAAAAA.xlsx", 400L),
            ("/i/outro-host/doc-DESKTOP-OUTRO999.xlsx", 400L));
        var vencedorD = "/i/doc-DESKTOP-HOSTAAAAA.xlsx"; // menor caminho ENTRE representantes
        VerificarEstabilidade(grupoD, new KeepMachine("HOSTAAAAA"), vencedorD, ordens, "D keep-machine");

        // ---- Cenário E: keep-manual — escolha explícita, plano estável -------------
        var grupoE = GrupoComMembros(
            mtimeIndice: 50,
            ("/j/um.txt", 10L),
            ("/j/dois.txt", 10L),
            ("/j/tres.txt", 10L));
        VerificarPlanoEstavel(grupoE, new KeepManual("/j/dois.txt"), ordens, "E keep-manual");
    }

    private static void VerificarEstabilidade(
        ConflictGroup grupo,
        ResolutionStrategy estrategia,
        string vencedorEsperado,
        int[] seeds,
        string cenario)
    {
        string? vencedorVisto = null;
        string[]? sacrificiosVistos = null;
        string? motivoEsperado = null;

        foreach (var seed in seeds)
        {
            var apresentacao = Apresentar(grupo.Members, seed);
            var grupoReordenado = new ConflictGroup(grupo.NormalizedBaseName, grupo.SizeBytes, apresentacao);

            // Despacho por tipo concreto (sobrecargas específicas de Resolution.Resolve).
            var plano = estrategia switch
            {
                KeepNewest => Resolution.Resolve(grupoReordenado, (KeepNewest)estrategia),
                KeepLargest => Resolution.Resolve(grupoReordenado, (KeepLargest)estrategia),
                KeepMachine maquina => Resolution.Resolve(grupoReordenado, maquina),
                _ => throw new InvalidOperationException($"estratégia sem despacho: {cenario}"),
            };

            Assert.Equal(vencedorEsperado, plano.Winner.Path);

            if (vencedorVisto is null)
            {
                vencedorVisto = plano.Winner.Path;
                sacrificiosVistos = plano.Sacrifices.Select(s => s.Entry.Path).ToArray();
            }
            else
            {
                Assert.Equal(vencedorVisto, plano.Winner.Path);
                Assert.Equal(
                    sacrificiosVistos,
                    plano.Sacrifices.Select(s => s.Entry.Path).ToArray());
            }

            // Sacrificados sempre em ordem canônica por caminho (ADR-0003).
            var sacrificios = plano.Sacrifices.Select(s => s.Entry.Path).ToArray();
            var ordenados = sacrificios.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.Equal(ordenados, sacrificios);

            // Motivo auditável coerente com a estratégia (calculado uma única vez).
            motivoEsperado ??= ReasonEsperado(estrategia);
            Assert.All(plano.Sacrifices, s => Assert.Equal(motivoEsperado, s.Reason));
        }
    }

    private static void VerificarPlanoEstavel(
        ConflictGroup grupo,
        KeepManual estrategia,
        int[] seeds,
        string cenario)
    {
        string? assinatura = null;

        foreach (var seed in seeds)
        {
            var apresentacao = Apresentar(grupo.Members, seed);
            var grupoReordenado = new ConflictGroup(grupo.NormalizedBaseName, grupo.SizeBytes, apresentacao);

            var plano = Resolution.Resolve(grupoReordenado, estrategia);

            Assert.Equal(estrategia.ChoicePath, plano.Winner.Path);

            var atual = plano.Winner.Path + "|" +
                        string.Join(";", plano.Sacrifices.Select(s => s.Entry.Path + ":" + s.Reason));

            assinatura ??= atual;
            Assert.Equal(assinatura, atual);
        }
    }

    private static IReadOnlyList<FileEntry> Apresentar(IReadOnlyList<FileEntry> membros, int seed) =>
        seed switch
        {
            0 => membros.ToArray(),
            -1 => membros.Reverse().ToArray(),
            _ => Shuffle(membros, seed),
        };

    private static string ReasonEsperado(ResolutionStrategy estrategia) => estrategia switch
    {
        KeepNewest => "keep-newest",
        KeepLargest => "keep-largest",
        KeepMachine k => $"keep-machine:{k.MachineId}",
        KeepManual => "keep-manual",
        _ => throw new InvalidOperationException("estratégia inesperada"),
    };

    /// <summary>
    /// Constrói grupo com FileEntries sintéticos: mtime DERIVADO do índice (fixo por
    /// cenário, UTC explícito — regra de fixture §5), sizes conforme os pares.
    /// </summary>
    private static ConflictGroup GrupoComMembros(
        int mtimeIndice,
        params (string Caminho, long Size)[] especificacoes)
    {
        var mtime = new DateTimeOffset(2026, 1, 1, 0, mtimeIndice, 0, TimeSpan.Zero);

        var membros = especificacoes
            .Select(spec => new FileEntry
            {
                Path = spec.Caminho,
                Size = spec.Size,
                MtimeUtc = mtime,
                Attributes = FileAttributes.Normal,
                VolumeId = "det-tie",
                FileId = spec.Caminho,
            })
            .ToArray();

        var baseNome = Path.GetFileName(especificacoes[0].Caminho);

        return new ConflictGroup(baseNome, especificacoes[0].Size, membros);
    }

    // =====================================================================================
    // DET-06 — golden file commitado
    // =====================================================================================

    [Fact]
    public void GoldenReport_FixtureMin_MatchesCommittedGoldenBytes()
    {
        var arvore = CriarArvorePadrao(); // FX-GOLDEN: mesma árvore 100% determinística

        var relatorio = GerarRelatorioV1(arvore.Raiz, arvore.Entradas.ToArray(), new Blake3Hasher());

        var texto = Encoding.UTF8.GetString(relatorio);
        var mascarado = MascararRaiz(texto, arvore.Raiz);
        var bytesComparaveis = Encoding.UTF8.GetBytes(mascarado);

        var goldenFonte = CaminhoGoldenFonte();
        var goldenImplantado = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "golden", "fx-golden-report-v1.json");

        if (Environment.GetEnvironmentVariable("DOCTOR_REGEN_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenFonte)!);
            File.WriteAllText(goldenFonte, mascarado, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return; // regeneração controlada: sair verde após gravar (skill tdd-deterministic-tooling)
        }

        if (!File.Exists(goldenImplantado) && !File.Exists(goldenFonte))
        {
            Assert.Fail(
                "DET-06 RED: golden ausente (tests/Doctor.Tests/Fixtures/golden/fx-golden-report-v1.json). " +
                "Gerar com: DOCTOR_REGEN_GOLDENS=1 dotnet test --filter FullyQualifiedName~DeterminismSuiteTests");
        }

        var goldenBytes = File.Exists(goldenImplantado)
            ? File.ReadAllBytes(goldenImplantado)
            : File.ReadAllBytes(goldenFonte);

        AssertBytesIdenticos(goldenBytes, bytesComparaveis, "DET-06 golden fx-golden-report-v1.json");
    }

    /// <summary>Diretório-fonte do golden (subida bin → projeto), para regeneração.</summary>
    private static string CaminhoGoldenFonte()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Doctor.Tests.csproj")))
            {
                return Path.Combine(dir.FullName, "Fixtures", "golden", "fx-golden-report-v1.json");
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("raiz do projeto de testes não localizada a partir do bin.");
    }

    /// <summary>
    /// Substitui APENAS o valor de generated_from.root_path (absoluto, varia por
    /// máquina/SO) pelo sentinela "&lt;RAIZ&gt;". Nenhuma outra parte do relatório é tocada.
    /// </summary>
    private static string MascararRaiz(string texto, string raiz)
    {
        var varianteSlash = raiz.Replace('\\', '/');
        var varianteBarra = raiz.Replace('/', '\\');

        texto = texto.Replace(varianteSlash, "<RAIZ>", StringComparison.Ordinal);
        if (varianteBarra != varianteSlash)
        {
            texto = texto.Replace(varianteBarra, "<RAIZ>", StringComparison.Ordinal);
        }

        return texto;
    }

    // =====================================================================================
    // Árvore padrão de 50 arquivos — 25 pareados + 10 conflitos + 15 únicos
    // =====================================================================================

    private sealed record ArvoreDet(string Raiz, IReadOnlyList<FileEntry> Entradas);

    /// <summary>
    /// Composição exigida pelo card, 100% determinística (nomes, sizes, conteúdos e
    /// mtimes derivam de índices):
    /// - 11 pares idênticos (22 arquivos) + 1 trio (3 arquivos) = 25 pareados;
    /// - 5 grupos de conflito real × 2 cópias = 10 (mesmas janelas parciais, miolos
    ///   distintos ⇒ sobrevivem ao L2 e divergem no L3);
    /// - 15 únicos (cinco deles com nomes-armadilha para DET-04).
    /// </summary>
    private ArvoreDet CriarArvorePadrao()
    {
        var criados = new List<(string Abs, int IndiceTempo)>();
        var relogio = 0;

        void Gravar(string relativo, byte[] conteudo)
        {
            var caminho = Path.Combine(new[] { _root }.Concat(relativo.Split('/')).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
            File.WriteAllBytes(caminho, conteudo);
            File.SetLastWriteTimeUtc(caminho, BaseTempo(relogio));
            criados.Add((caminho, relogio));
            relogio++;
        }

        // ---- 11 pares idênticos -----------------------------------------------------
        for (var i = 0; i < 11; i++)
        {
            var grande = i % 2 == 0; // metade grande (janelas), metade pequena (inteiro)
            var tamanho = grande ? ArquivoGrande : (16 + i) * Kib;
            var conteudo = ConteudoPadronizado(semente: (byte)(0x30 + i), tamanho);

            Gravar($"dups/p{i:00}/foto-p{i:00}.jpg", conteudo);
            Gravar($"backup/p{i:00}/foto-p{i:00}.jpg", (byte[])conteudo.Clone());
        }

        // ---- trio idêntico -----------------------------------------------------------
        var conteudoTrio = ConteudoPadronizado(semente: (byte)0xE0, ArquivoGrande + 7);
        Gravar("trio/arq-trio.bin", conteudoTrio);
        Gravar("trio/copia1/arq-trio.bin", (byte[])conteudoTrio.Clone());
        Gravar("trio/copia2/arq-trio.bin", (byte[])conteudoTrio.Clone());

        // ---- 5 grupos de conflito real (2 cópias cada) --------------------------------
        for (var k = 0; k < 5; k++)
        {
            var host = $"HOST0{k}"; // 6 caracteres alfanuméricos (contrato do normalizador)
            // Plain: miolo (0x10+k, 0x50+k); mirror: miolo invertido (0x50+k, 0x10+k).
            // Janelas idênticas (cabeça/cauda) → colisão parcial garantida.
            // Miolo distinto → hash completo divergente → conflito real.
            Gravar($"conflicts/orc-k{k:00}.bin", ConteudoConflito(mioloPlain: (byte)(0x10 + k), mioloMirror: (byte)(0x50 + k)));
            Gravar($"conflicts/mirror/orc-k{k:00}-DESKTOP-{host}.bin", ConteudoConflito(mioloPlain: (byte)(0x50 + k), mioloMirror: (byte)(0x10 + k)));
        }

        // ---- 15 únicos (cinco primeiros = nomes-armadilha de DET-04) -------------------
        string[] nomesArmadilha =
        [
            "Zebra.txt",
            "apple.txt",
            "Apple.txt",
            "zebra.txt",
            "Árvore.txt",
        ];

        for (var u = 0; u < 15; u++)
        {
            var nome = u < nomesArmadilha.Length ? nomesArmadilha[u] : $"arq-u{u:00}.txt";
            var tamanho = 5000 + (u * 997);
            Gravar($"unique/{nome}", ConteudoPadronizado(semente: (byte)(0x70 + u), tamanho));
        }

        // Entradas Level 0 na ordem de CRIAÇÃO (= ordem física "real" da prova).
        var entradas = criados
            .Select(par =>
            {
                var info = new FileInfo(par.Abs);
                return new FileEntry
                {
                    Path = par.Abs,
                    Size = info.Length,
                    MtimeUtc = new DateTimeOffset(BaseTempo(par.IndiceTempo), TimeSpan.Zero),
                    Attributes = FileAttributes.Normal,
                    VolumeId = "det-suite",
                    FileId = par.Abs,
                };
            })
            .ToList();

        Assert.Equal(50, entradas.Count); // composição do card: 25 + 10 + 15

        return new ArvoreDet(_root, entradas);
    }

    /// <summary>Universo EM MEMÓRIA das mesmas 50 entradas (unit: DET-04/05 sem disco).</summary>
    private static List<FileEntry> UniversoEmMemoria()
    {
        var lista = new List<FileEntry>();
        var indice = 0;

        void Adicionar(string relativo, long tamanho)
        {
            lista.Add(new FileEntry
            {
                Path = "/universo-det/" + relativo,
                Size = tamanho,
                MtimeUtc = BaseTempoOffset(indice),
                Attributes = FileAttributes.Normal,
                VolumeId = "det-suite",
                FileId = relativo,
            });
            indice++;
        }

        for (var i = 0; i < 11; i++)
        {
            var tamanho = i % 2 == 0 ? ArquivoGrande : (16 + i) * Kib;
            Adicionar($"dups/p{i:00}/foto-p{i:00}.jpg", tamanho);
            Adicionar($"backup/p{i:00}/foto-p{i:00}.jpg", tamanho);
        }

        Adicionar("trio/arq-trio.bin", ArquivoGrande + 7);
        Adicionar("trio/copia1/arq-trio.bin", ArquivoGrande + 7);
        Adicionar("trio/copia2/arq-trio.bin", ArquivoGrande + 7);

        for (var k = 0; k < 5; k++)
        {
            Adicionar($"conflicts/orc-k{k:00}.bin", ArquivoGrande);
            Adicionar($"conflicts/mirror/orc-k{k:00}-DESKTOP-HOST0{k}.bin", ArquivoGrande);
        }

        string[] nomesArmadilha = ["Zebra.txt", "apple.txt", "Apple.txt", "zebra.txt", "Árvore.txt"];
        for (var u = 0; u < 15; u++)
        {
            var nome = u < nomesArmadilha.Length ? nomesArmadilha[u] : $"arq-u{u:00}.txt";
            Adicionar($"unique/{nome}", 5000 + (u * 997));
        }

        Assert.Equal(50, lista.Count);
        return lista;
    }

    private static DateTime BaseTempo(int indice) => BaseTempoOffset(indice).UtcDateTime;

    private static DateTimeOffset BaseTempoOffset(int indice) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(indice);

    private static byte[] ConteudoPadronizado(byte semente, int tamanho)
    {
        var bytes = new byte[tamanho];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(semente + (i % 251));
        }

        return bytes;
    }

    /// <summary>Cabeça e cauda fixas (colisão parcial garantida); dois miolos distintos.</summary>
    private static byte[] ConteudoConflito(byte mioloPlain, byte mioloMirror)
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

        var meio = ArquivoGrande - Janela;
        for (var i = Janela; i < meio; i++)
        {
            // Miolo alterna por BLOCOS de 4096 bytes entre os dois valores: as cópias
            // mantêm janelas parciais [0,64K)+[fim-64K,fim) idênticas (mesma semente de
            // cabeça/cauda) e hashes completos garantidamente distintos.
            bytes[i] = ((i / 4096) % 2 == 0) ? mioloPlain : mioloMirror;
        }

        return bytes;
    }

    // =====================================================================================
    // Execução do pipeline + projeções determinísticas
    // =====================================================================================

    private sealed record SaidaDuasCamadas(byte[] ResultadoCanonico, byte[] RelatorioV1, ScanTelemetry Telemetria);

    private ScanPipeline RodarPipeline(FileEntry[] ordemFisica, IHasher hasher) =>
        new(new FakeFileEnumerator(ordemFisica), hasher);

    private SaidaDuasCamadas ExecutarScanDuasCamadas(string raiz, FileEntry[] ordemFisica, IHasher hasher)
    {
        var resultado = RodarPipeline(ordemFisica, hasher).Run(raiz);

        var canonico = JsonSerializer.SerializeToUtf8Bytes(resultado, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        var relatorio = GerarRelatorioV1(raiz, ordemFisica, hasher);

        return new SaidaDuasCamadas(canonico, relatorio, MontarTelemetria(resultado, ordemFisica.Length));
    }

    /// <summary>
    /// Relatório schema v1 completo com timestamps CONGELADOS (condição §2.3) — o único
    /// tempo permitido vem dos argumentos fixos, nunca do relógio.
    /// </summary>
    private byte[] GerarRelatorioV1(string raiz, FileEntry[] ordemFisica, IHasher hasher)
    {
        var resultado = RodarPipeline(ordemFisica, hasher).Run(raiz);
        var telemetria = MontarTelemetria(resultado, ordemFisica.Length);

        using var saida = new MemoryStream();
        new ReportWriterJson().Write(
            resultado,
            telemetria,
            PlaceholderReport.Records(resultado.Groups.SelectMany(g => g.Members)),
            raiz,
            CongeladoInicio,
            CongeladoFim,
            saida);

        return saida.ToArray();
    }

    /// <summary>
    /// Telemetria derivada das decisões registradas (mesmo contrato do T16/ScanCommand):
    /// parcial para todo membro de grupo candidato, completo para os grupos com veredito,
    /// receita de bytes do ADR-0005 via constantes do hasher.
    /// </summary>
    private static ScanTelemetry MontarTelemetria(ScanResult resultado, int enumerados)
    {
        var membrosL2 = resultado.Groups
            .Where(g => g.Members.Count >= 2)
            .SelectMany(g => g.Members)
            .ToArray();

        var tamanhosL3 = resultado.IdenticalDuplicates
            .SelectMany(d => d.Files.Select(f => f.Size))
            .Concat(resultado.RealConflicts.SelectMany(c => c.Files.Select(_ => c.SizeBytes)))
            .ToArray();

        return new ScanTelemetry
        {
            FilesEnumerated = enumerados,
            FilesSkipped = 0,
            FilesPlaceholder = 0,
            FilesPartialHashed = membrosL2.Length,
            FilesFullHashed = tamanhosL3.Length,
            BytesReadPartial = membrosL2.Sum(BytesParciais),
            BytesReadFull = tamanhosL3.Sum(),
            PlaceholderBytesRead = 0,
        };
    }

    /// <summary>Receita v1 do ADR-0005: ≤ 128 KiB lê inteiro; acima, duas janelas de 64 KiB.</summary>
    private static long BytesParciais(FileEntry arquivo) =>
        arquivo.Size <= Blake3Hasher.WholeFileLimitBytes
            ? arquivo.Size
            : 2L * Blake3Hasher.WindowBytes;

    // =====================================================================================
    // Comparação byte-a-byte com diff legível
    // =====================================================================================

    /// <summary>
    /// Compara dois vetores de bytes; em caso de divergência, FALHA com diff: tamanhos,
    /// primeiro offset divergente, trecho hexadecimal ao redor e até 12 linhas de texto
    /// divergentes de cada lado (JSON é UTF-8 legível).
    /// </summary>
    private static void AssertBytesIdenticos(byte[] esperado, byte[] obtido, string contexto)
    {
        if (esperado.AsSpan().SequenceEqual(obtido))
        {
            return;
        }

        var mensagem = new StringBuilder();
        mensagem.AppendLine($"[{contexto}] saídas DIFEREM byte a byte.");
        mensagem.AppendLine($"bytes esperados: {esperado.Length}; bytes obtidos: {obtido.Length}.");

        var comun = Math.Min(esperado.Length, obtido.Length);
        var primeiro = 0;
        while (primeiro < comun && esperado[primeiro] == obtido[primeiro])
        {
            primeiro++;
        }

        mensagem.AppendLine($"primeiro byte divergente no offset {primeiro}.");

        const int contexto_ = 32;
        var inicio = Math.Max(0, primeiro - contexto_);
        var fimEsperado = Math.Min(esperado.Length, primeiro + contexto_);
        var fimObtido = Math.Min(obtido.Length, primeiro + contexto_);

        mensagem.AppendLine($"esperado [{inicio}..{fimEsperado}): {Hex(esperado, inicio, fimEsperado)}");
        mensagem.AppendLine($"obtido   [{inicio}..{fimObtido}): {Hex(obtido, inicio, fimObtido)}");

        foreach (var linha in DiffLinhas(esperado, obtido))
        {
            mensagem.AppendLine(linha);
        }

        Assert.Fail(mensagem.ToString());
    }

    private static string Hex(byte[] bytes, int inicio, int fim)
    {
        var sb = new StringBuilder();
        for (var i = inicio; i < fim; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
            if ((i - inicio + 1) % 16 == 0)
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> DiffLinhas(byte[] esperado, byte[] obtido)
    {
        var linhasEsperadas = Encoding.UTF8
            .GetString(esperado)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        var linhasObtidas = Encoding.UTF8
            .GetString(obtido)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        const int maxLinhas = 12;
        var mostradas = 0;
        var total = Math.Max(linhasEsperadas.Length, linhasObtidas.Length);

        for (var i = 0; i < total && mostradas < maxLinhas; i++)
        {
            var linhaE = i < linhasEsperadas.Length ? linhasEsperadas[i] : "<ausente>";
            var linhaO = i < linhasObtidas.Length ? linhasObtidas[i] : "<ausente>";

            if (!string.Equals(linhaE, linhaO, StringComparison.Ordinal))
            {
                yield return $"diff @{i}: esperado | {linhaE}";
                yield return $"diff @{i}: obtido   | {linhaO}";
                mostradas += 2;
            }
        }

        if (mostradas >= maxLinhas)
        {
            yield return "diff truncado em 12 linhas divergentes.";
        }
    }

    // =====================================================================================
    // Utilitários
    // =====================================================================================

    /// <summary>Fisher-Yates com seed fixa — determinístico, sem aleatoriedade real.</summary>
    private static FileEntry[] Shuffle(IEnumerable<FileEntry> original, int seed)
    {
        var copia = original.ToArray();
        var random = new Random(seed);

        for (var i = copia.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copia[i], copia[j]) = (copia[j], copia[i]);
        }

        return copia;
    }

    /// <summary>Oráculo INDEPENDENTE do produto: ordem lexicográfica de bytes UTF-8.</summary>
    private static int ComparaPorBytesUtf8(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        var comun = Math.Min(bytesA.Length, bytesB.Length);

        for (var i = 0; i < comun; i++)
        {
            if (bytesA[i] != bytesB[i])
            {
                return bytesA[i].CompareTo(bytesB[i]);
            }
        }

        return bytesA.Length.CompareTo(bytesB.Length);
    }

    /// <summary>
    /// Enumerador fake (L0): devolve as entradas EXATAMENTE na ordem física pedida —
    /// direta, reversa ou embaralhada. A ordenação canônica é responsabilidade do
    /// pipeline, e é isso que DET-01/02/03 verificam (padrão T12).
    /// </summary>
    private sealed class FakeFileEnumerator : IFileEnumerator
    {
        private readonly IReadOnlyList<FileEntry> _ordem;

        public FakeFileEnumerator(IReadOnlyList<FileEntry> ordem) => _ordem = ordem;

        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) =>
            new(_ordem, Array.Empty<ScanError>(), new ScanTelemetry());
    }
}
