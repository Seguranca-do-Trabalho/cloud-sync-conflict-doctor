namespace Doctor.Tests;

using System.Text.Json;
using Doctor.Core;
using Xunit;

/// <summary>
/// S11-6a (card t_218a0218) — SEG-01 e SEG-08 da matriz GATE 5
/// (docs/security-audit-gate5.md §2; adendos T-12a/T-12b §4.1):
///
/// SEG-01 — <see cref="Security_PathTraversal_HostileName_ContainedInRoot"/>
/// (threat-model T-01, regra R2): contenção byte-a-byte pelo prefixo canônico da
/// raiz ANTES de cada move/restore. Árvore com nome trailing dot/space (vetor \\?\),
/// nome RLO U+202E e homóglifo cirílico passa por quarentena E restore; manifesto
/// forjado com original_path fora da raiz é RECUSADO sem tocar nada; nenhum caminho
/// fora da raiz é tocado na operação inteira.
///
/// SEG-08 — <see cref="Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack"/>
/// (threat-model T-04, regras R4/R5; contrato R5): hook injeta troca de conteúdo
/// ENTRE o hash pré-move e o move; afirma rollback executado, fonte de volta na
/// origem, operação FALHA (nada declarado sucesso) e evidência auditable
/// hash_pre_move != hash_post_move no manifesto parcial.
/// </summary>
public sealed class SecurityContainmentTests : IDisposable
{
    private static readonly DateTimeOffset TimestampCongelado =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTime EntradaFixaMtime =
        new(2026, 8, 20, 10, 30, 0, DateTimeKind.Utc);

    private readonly string _root;

    /// <summary>Diretório isca FORA da raiz: qualquer fuga de contenção toca estes bytes.</summary>
    private readonly string _foraDaRaiz;

    public SecurityContainmentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"s116a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _foraDaRaiz = Path.Combine(Path.GetTempPath(), $"s116a-fora-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_foraDaRaiz);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _foraDaRaiz })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // limpeza best-effort: tmp do SO recolhe depois
            }
        }
    }

    // ------------------------------------------------------------------
    // SEG-01 (T-12a / R2): contenção byte-a-byte sob nomes hostis
    // ------------------------------------------------------------------
    [Fact]
    public void Security_PathTraversal_HostileName_ContainedInRoot()
    {
        // Vetores do T-01 criáveis no filesystem POSIX: trailing dot/space (o Win32
        // sem \\?\ resolve como OUTRO arquivo), RLO U+202E (nome exibido engana) e
        // homóglifo cirílico ('а' U+0430 ≠ 'a' latino).
        var trailingDotSpace = CriarArquivo("evil.txt. ", Conteudo(0xE1));
        var rlo = CriarArquivo("\u202Eexe.pdf", Conteudo(0xE2));
        var homoglifo = CriarArquivo("\u0430rquivo.txt", Conteudo(0xE3));
        var nomesHostis = new[] { trailingDotSpace, rlo, homoglifo };

        // Isca fora da raiz: deve permanecer intoca durante TODA a operação.
        var isca = Path.Combine(_foraDaRaiz, "isca.txt");
        File.WriteAllBytes(isca, [0xCA, 0xFE]);
        var iscaBytes = File.ReadAllBytes(isca);
        var iscaMtime = File.GetLastWriteTimeUtc(isca);

        var svc = new QuarantineService();

        // ---- ato 1: quarentena + restore executam pelos caminhos hostis ------------
        var resultado = svc.Move(
            nomesHostis.Select(c => new QuarantineItem(Entrada(c), "IDENTICAL_DUPLICATE", "KEEP_NEWEST")).ToArray(),
            PlanoPadrao());
        Assert.Equal(3, resultado.MovedPaths.Count);

        var restauracao = svc.Restore(resultado.OperationId, _root);
        Assert.Equal(3, restauracao.RestoredPaths.Count);

        // contenção byte-a-byte: todo caminho tocado começa pelo prefixo canônico
        // da raiz (comparação Ordinal sobre forma plena, separador final garantido).
        var prefixoRaiz = ComSeparadorFinal(Path.GetFullPath(_root));
        Assert.All(
            restauracao.RestoredPaths.Concat(resultado.MovedPaths),
            c => Assert.True(
                Path.GetFullPath(c).StartsWith(prefixoRaiz, StringComparison.Ordinal),
                $"caminho tocado fora da raiz canônica: {c}"));

        // nenhum caminho fora da raiz foi tocado: isca única, bytes e mtime intactos
        Assert.Equal(new[] { isca }, Directory.GetFiles(_foraDaRaiz, "*", SearchOption.AllDirectories));
        Assert.Equal(iscaBytes, File.ReadAllBytes(isca));
        Assert.Equal(iscaMtime, File.GetLastWriteTimeUtc(isca));

        // nomes hostis preservados EXATAMENTE (T-01 mitigação (c): sem correção silenciosa)
        Assert.All(nomesHostis, c => Assert.True(File.Exists(c), $"hostil não restaurado: {c}"));
        using var json = JsonDocument.Parse(File.ReadAllBytes(resultado.ManifestPath));
        var caminhosNoManifesto = json.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("original_path").GetString())
            .ToArray();
        Assert.Equal(
            nomesHostis.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            caminhosNoManifesto);

        // ---- ato 2: manifesto FORJADO com original_path fora da raiz é recusado ----
        // A quarentena é metadado da própria ferramenta, mas o restore não confia em
        // nada: destino fora do prefixo canônico ⇒ falha fechada ANTES de qualquer toque.
        var vitima = CriarArquivo("docs/vitima.txt", Conteudo(0xE4));
        var operacaoIsca = svc.Move(
            [new QuarantineItem(Entrada(vitima), "REAL_CONFLICT", "KEEP_NEWEST")],
            PlanoPadrao());

        var destinoForjado = Path.Combine(_foraDaRaiz, "fuga.txt");
        ReescreverOriginalPath(operacaoIsca.ManifestPath, destinoForjado);

        Assert.Throws<Doctor.Core.QuarantineContainmentException>(
            () => svc.Restore(operacaoIsca.OperationId, _root));

        // fail-closed: NADA escrito fora da raiz, payload permanece na quarentena
        Assert.False(File.Exists(destinoForjado), "restore forjado escreveu fora da raiz");
        Assert.True(File.Exists(PayloadUnico(operacaoIsca)), "payload sumiu sem rollback de leitura");
        Assert.Equal(new[] { isca }, Directory.GetFiles(_foraDaRaiz, "*", SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------------
    // SEG-08 (T-12b / R5): troca de conteúdo na janela hash→move ⇒ rollback
    // ------------------------------------------------------------------
    [Fact]
    public void Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack()
    {
        var conteudoBom = Conteudo(0xB0);
        var conteudoMau = Conteudo(0xD1);
        var hashBom = Convert.ToHexString(Blake3.Hasher.Hash(conteudoBom).AsSpan()).ToLowerInvariant();
        var hashMau = Convert.ToHexString(Blake3.Hasher.Hash(conteudoMau).AsSpan()).ToLowerInvariant();
        Assert.NotEqual(hashBom, hashMau);

        var caminho = CriarArquivo("docs/relatorio.docx", conteudoBom);
        var entrada = Entrada(caminho); // snapshot L0 capturado sobre o conteúdo BOM

        // Hook da JANELA T-04: o hash pré-move lê via openReadOverride (cadeia do
        // gate — conteúdo bom); o moveOverride troca o conteúdo no caminho ANTES do
        // File.Move. A troca acontece UMA única vez — exatamente entre o hash
        // pré-move e o move; o rollback passa pelo mesmo _move sem retrigar.
        var trocou = false;
        var svc = new QuarantineService(
            openReadOverride: path => path == caminho
                ? new MemoryStream(conteudoBom)
                : File.OpenRead(path),
            moveOverride: (origem, destino) =>
            {
                if (!trocou)
                {
                    trocou = true;
                    Assert.Equal(caminho, origem);
                    File.WriteAllBytes(caminho, conteudoMau); // troca na janela
                }

                File.Move(origem, destino);
            });

        var excecao = Assert.Throws<QuarantineRollbackException>(() => svc.Move(
            [new QuarantineItem(entrada, "IDENTICAL_DUPLICATE", "KEEP_NEWEST")],
            PlanoPadrao()));

        Assert.Equal(caminho, excecao.OriginalPath);

        // rollback executado: a fonte existe DE VOLTA na origem (sem rollback o
        // arquivo teria ficado na quarentena); os bytes presentes são exatamente os
        // que foram movidos e devolvidos — prova do vaivém completo.
        Assert.True(File.Exists(caminho));
        Assert.Equal(conteudoMau, File.ReadAllBytes(caminho));

        // nada declarado sucesso: nenhum diretório definitivo <op_id> foi publicado
        var raizQuarentena = Path.Combine(_root, "ConflictDoctor", "quarantine");
        string[] publicados = Directory.Exists(raizQuarentena)
            ? Directory.GetDirectories(raizQuarentena)
                .Where(d => !Path.GetFileName(d).StartsWith("staging-", StringComparison.Ordinal))
                .ToArray()
            : [];
        Assert.Empty(publicados);

        // evidência auditável no manifesto parcial: status FALHA +
        // hash_pre_move ≠ hash_post_move registrados (contrato R5)
        using var json = JsonDocument.Parse(File.ReadAllBytes(excecao.PartialManifestPath));
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Equal(caminho, item.GetProperty("original_path").GetString());
        Assert.Equal(hashBom, item.GetProperty("hash_pre_move").GetString());
        Assert.Equal(hashMau, item.GetProperty("hash_post_move").GetString());
        Assert.NotEqual(
            item.GetProperty("hash_pre_move").GetString(),
            item.GetProperty("hash_post_move").GetString());

        // a isca externa continua intoca
        Assert.Empty(Directory.GetFiles(_foraDaRaiz, "*", SearchOption.AllDirectories));
    }

    // ==================================================================
    // infraestrutura do teste
    // ==================================================================

    private QuarantinePlan PlanoPadrao() => new(_root, TimestampCongelado);

    private static string ComSeparadorFinal(string diretorio) =>
        diretorio.EndsWith(Path.DirectorySeparatorChar)
            ? diretorio
            : diretorio + Path.DirectorySeparatorChar;

    private static byte[] Conteudo(byte semente) =>
        Enumerable.Range(0, 2048).Select(i => (byte)(semente + (i % 89))).ToArray();

    private string CriarArquivo(string caminhoRelativo, byte[] conteudo)
    {
        var absoluto = Path.Combine(_root, caminhoRelativo);
        Directory.CreateDirectory(Path.GetDirectoryName(absoluto)!);
        File.WriteAllBytes(absoluto, conteudo);
        File.SetLastWriteTimeUtc(absoluto, EntradaFixaMtime);
        return absoluto;
    }

    private FileEntry Entrada(string caminho)
    {
        var info = new FileInfo(caminho);
        return new FileEntry
        {
            Path = caminho,
            Size = info.Length,
            MtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc),
            Attributes = FileAttributes.Normal,
            VolumeId = "s116a-volume",
            FileId = caminho,
        };
    }

    private static void ReescreverOriginalPath(string manifestPath, string novoDestino)
    {
        // Forja o manifesto trocando APENAS o original_path do primeiro item
        // (simula o vetor do T-01: consumidor não pode confiar no metadado).
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var raiz = doc.RootElement;

        var manifesto = new Dictionary<string, object?>();
        foreach (var prop in raiz.EnumerateObject())
        {
            if (prop.Name != "items")
            {
                manifesto[prop.Name] = prop.Value.Clone();
            }
        }

        var itens = new List<Dictionary<string, object?>>();
        var primeiro = true;
        foreach (var item in raiz.GetProperty("items").EnumerateArray())
        {
            var dict = new Dictionary<string, object?>();
            foreach (var prop in item.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }

            if (primeiro)
            {
                dict["original_path"] = novoDestino;
                primeiro = false;
            }

            itens.Add(dict);
        }

        manifesto["items"] = itens;
        File.WriteAllBytes(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifesto));
    }

    private static string PayloadUnico(QuarantineOperationResult movimento)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(movimento.ManifestPath));
        var relativo = json.RootElement.GetProperty("items")[0]
            .GetProperty("quarantine_path").GetString()!;
        return Path.Combine(
            movimento.QuarantineDirectory,
            relativo.Replace('/', Path.DirectorySeparatorChar));
    }
}
