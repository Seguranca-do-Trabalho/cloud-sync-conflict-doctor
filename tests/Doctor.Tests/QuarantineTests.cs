namespace Doctor.Tests;

using System.Text.RegularExpressions;
using Doctor.Core;
using Xunit;

/// <summary>
/// T15 (t_5554ef78) — EPIC 08: quarentena + restore (SPEC §18, §22; ADR-0002;
/// ADR-0010; docs/contratos.md). Regras provadas aqui:
/// 1. Move ⇒ original sai, payload datado existe, manifesto existe e carrega TODOS
///    os campos do ADR-0010 §2, hash BLAKE3 conferido contra o payload;
/// 2. Restore ⇒ original de volta byte-idêntico, hash preservado (§22);
/// 3. Restore sobre destino ocupado ⇒ JAMAIS sobrescreve: falha com exceção e
///    nada é tocado (ocupante, payload e manifesto permanecem byte-idênticos);
/// 4. Homônimos em pastas distintas ⇒ payload sem colisão, manifesto ordenado
///    por caminho em bytes UTF-8 (ADR-0003 regra 1);
/// 5. Mesmo estado + mesmo timestamp ⇒ mesmo operation_id e manifesto
///    byte-idêntico (determinismo ADR-0010 §1);
/// 6. Metadado stale entre L0 e move ⇒ item pulado, arquivo fica onde está,
///    registro honesto (ADR-0010 §3 — revalidação TOCTOU);
/// 7. Falha no meio do move ⇒ manifesto parcial honesto + exceção; nada movido
///    fica sem registro (ADR-0002 item 4, ADR-0010 §3);
/// 8. Guarda estática: ZERO APIs de deleção em Doctor.Core (§22; ADR-0002 item 5).
/// </summary>
public sealed class QuarantineTests : IDisposable
{
    private readonly string _root;

    public QuarantineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"t15-{Guid.NewGuid():N}");
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
            // limpeza best-effort: tmp do SO recolhe depois
        }
    }

    // ------------------------------------------------------------------
    // NDES-01 — move completo: quarentena + manifesto + hash confere
    // ------------------------------------------------------------------
    [Fact]
    public void Move_OriginalSai_PayloadEManifestoExistem_HashConferido()
    {
        var caminho = CriarArquivo("docs/relatorio.txt", Conteudo(0x51));
        var svc = NovoServico();

        var resultado = svc.Move(
            [new QuarantineItem(Entrada(caminho), "IDENTICAL_DUPLICATE", "KEEP_NEWEST")],
            PlanoPadrao());

        // original saiu; estrutura §18/ADR-0010 existe
        Assert.False(File.Exists(caminho));
        Assert.True(Directory.Exists(resultado.QuarantineDirectory));
        Assert.True(File.Exists(resultado.ManifestPath));
        Assert.StartsWith(
            Path.Combine(_root, "ConflictDoctor", "quarantine") + Path.DirectorySeparatorChar,
            resultado.QuarantineDirectory);
        Assert.Equal("completed", resultado.Status);
        Assert.Single(resultado.MovedPaths);

        // manifesto: campos EXATOS do ADR-0010 §2
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(resultado.ManifestPath));
        var raiz = json.RootElement;
        Assert.Equal(
            new[] { "manifest_version", "operation_id", "created_utc", "items", "status" },
            raiz.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, raiz.GetProperty("manifest_version").GetInt32());
        Assert.Equal("completed", raiz.GetProperty("status").GetString());

        var item = raiz.GetProperty("items")[0];
        Assert.Equal(
            new[] { "original_path", "quarantine_path", "size", "mtime_utc", "hash",
                    "algorithm", "hash_version", "reason", "rule" },
            item.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(caminho, item.GetProperty("original_path").GetString());
        Assert.Equal(Conteudo(0x51).LongLength, item.GetProperty("size").GetInt64());
        Assert.Equal("BLAKE3", item.GetProperty("algorithm").GetString());
        Assert.Equal(1, item.GetProperty("hash_version").GetInt32());
        Assert.Equal("IDENTICAL_DUPLICATE", item.GetProperty("reason").GetString());
        Assert.Equal("KEEP_NEWEST", item.GetProperty("rule").GetString());

        // hash do manifesto == BLAKE3 recalculado INDEPENDENTEMENTE do payload
        var payloadAbsoluto = Path.Combine(
            resultado.QuarantineDirectory,
            item.GetProperty("quarantine_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(payloadAbsoluto));
        var hashIndependente = Convert.ToHexString(
            Blake3.Hasher.Hash(File.ReadAllBytes(payloadAbsoluto)).AsSpan()).ToLowerInvariant();
        Assert.Equal(hashIndependente, item.GetProperty("hash").GetString());

        // operation_id no formato ADR-0010 §1: yyyyMMddTHHmmssZ-8hex
        Assert.Matches(@"^\d{8}T\d{6}Z-[0-9a-f]{8}$", raiz.GetProperty("operation_id").GetString());
    }

    // ------------------------------------------------------------------
    // NDES-02 — restore: original de volta byte-idêntico, hash preservado
    // ------------------------------------------------------------------
    [Fact]
    public void Restore_AposMover_OriginalDeVoltaByteIdentico_HashPreservado()
    {
        var caminho = CriarArquivo("docs/relatorio.txt", Conteudo(0x77));
        var conteudoOriginal = File.ReadAllBytes(caminho);
        var svc = NovoServico();

        var movimento = svc.Move(
            [new QuarantineItem(Entrada(caminho), "REAL_CONFLICT", "KEEP_LARGEST")],
            PlanoPadrao());

        var restauracao = svc.Restore(movimento.OperationId, _root);

        Assert.True(File.Exists(caminho));
        Assert.Equal(conteudoOriginal, File.ReadAllBytes(caminho));

        // hash preservado: mesmo conteúdo ⇒ mesmo BLAKE3 registrado no manifesto
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(movimento.ManifestPath));
        var hashNoManifesto = json.RootElement.GetProperty("items")[0].GetProperty("hash").GetString();
        var hashRestaurado = Convert.ToHexString(
            Blake3.Hasher.Hash(File.ReadAllBytes(caminho)).AsSpan()).ToLowerInvariant();
        Assert.Equal(hashRestaurado, hashNoManifesto);

        // histórico nunca apagado: manifesto marca restauração
        Assert.Equal("restored", json.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(restauracao.OperationId, movimento.OperationId);
        Assert.Equal(caminho, restauracao.RestoredPaths.Single());
    }

    // ------------------------------------------------------------------
    // NDES-03 — restore com destino ocupado: exceção, NADA é tocado
    // ------------------------------------------------------------------
    [Fact]
    public void Restore_DestinoOcupado_FalhaComExcecao_SemTocarNada()
    {
        var caminho = CriarArquivo("docs/a.txt", Conteudo(0x11));
        var svc = NovoServico();

        var movimento = svc.Move(
            [new QuarantineItem(Entrada(caminho), "IDENTICAL_DUPLICATE", "KEEP_NEWEST")],
            PlanoPadrao());

        // ocupante diferente ocupa o caminho original
        var ocupante = Conteudo(0xDE);
        File.WriteAllBytes(caminho, ocupante);

        var bytesOcupante = File.ReadAllBytes(caminho);
        var payloadAntes = File.ReadAllBytes(PayloadUnico(movimento));
        var manifestAntes = File.ReadAllBytes(movimento.ManifestPath);

        var excecao = Assert.Throws<RestoreConflictException>(
            () => svc.Restore(movimento.OperationId, _root));

        Assert.Contains(caminho, excecao.Message);

        // nada foi tocado: ocupante intacto, payload intacto, manifesto intacto
        Assert.Equal(bytesOcupante, File.ReadAllBytes(caminho));
        Assert.NotEqual(Conteudo(0x11), bytesOcupante);
        Assert.Equal(payloadAntes, File.ReadAllBytes(PayloadUnico(movimento)));
        Assert.Equal(manifestAntes, File.ReadAllBytes(movimento.ManifestPath));
    }

    // ------------------------------------------------------------------
    // homônimos: payload sem colisão + manifesto ordenado por caminho
    // ------------------------------------------------------------------
    [Fact]
    public void Move_HomonimosEmPastasDistintas_PayloadDistinto_ManifestoOrdenado()
    {
        var b = CriarArquivo("b/nota.txt", Conteudo(0x02));
        var a = CriarArquivo("a/nota.txt", Conteudo(0x01));
        var svc = NovoServico();

        var resultado = svc.Move(
            [
                new QuarantineItem(Entrada(b), "IDENTICAL_DUPLICATE", "KEEP_NEWEST"),
                new QuarantineItem(Entrada(a), "IDENTICAL_DUPLICATE", "KEEP_NEWEST"),
            ],
            PlanoPadrao());

        Assert.Equal(2, resultado.MovedPaths.Count);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(resultado.ManifestPath));
        var itens = json.RootElement.GetProperty("items");
        Assert.Equal(2, itens.GetArrayLength());

        // ordem canônica por caminho em bytes UTF-8, nunca ordem de chamada
        var caminhos = itens.EnumerateArray().Select(i => i.GetProperty("original_path").GetString()).ToArray();
        Assert.Equal(new[] { a, b }, caminhos);
        Assert.Equal(caminhos, caminhos.OrderBy(p => p, StringComparer.Ordinal).ToArray());

        // nomes opacos distintos no payload (ADR-0010 §1)
        var payloads = itens.EnumerateArray().Select(i => i.GetProperty("quarantine_path").GetString()).ToArray();
        Assert.NotEqual(payloads[0], payloads[1]);
        Assert.All(payloads, p => Assert.Matches(@"^payload/\d{4}\.dat$", p!));
    }

    // ------------------------------------------------------------------
    // determinismo: mesmo estado ⇒ mesmo operation_id e manifesto igual
    // ------------------------------------------------------------------
    [Fact]
    public void Move_MesmoEstadoDuasVezes_MesmoOperationId_ManifestoByteIdentico()
    {
        var caminho = CriarArquivo("docs/x.bin", Conteudo(0xC3));
        var svc = NovoServico();
        var plano = PlanoPadrao(); // timestamp congelado no plano

        var primeira = svc.Move([new QuarantineItem(Entrada(caminho), "R", "RULE")], plano);
        var manifestPrimeiro = File.ReadAllBytes(primeira.ManifestPath);

        // devolve o arquivo ao estado pré-operação e limpa a quarentena:
        // segunda execução parte do MESMO estado absoluto (mesmo caminho)
        svc.Restore(primeira.OperationId, _root);
        Directory.Delete(Path.Combine(_root, "ConflictDoctor"), recursive: true);
        File.SetLastWriteTimeUtc(caminho, EntradaFixaMtime);

        var segunda = svc.Move([new QuarantineItem(Entrada(caminho), "R", "RULE")], plano);

        Assert.Equal(primeira.OperationId, segunda.OperationId);
        Assert.Equal(manifestPrimeiro, File.ReadAllBytes(segunda.ManifestPath));
    }

    // ------------------------------------------------------------------
    // TOCTOU (ADR-0010 §3): metadado stale ⇒ pula, não move, registra
    // ------------------------------------------------------------------
    [Fact]
    public void Move_MetadadoStale_ItemPulado_ArquivoPermaneceOndeEsta()
    {
        var caminho = CriarArquivo("docs/stale.txt", Conteudo(0x33));
        var entrada = Entrada(caminho);

        // árvore muda DEPOIS do snapshot L0: tamanho diverge
        File.WriteAllBytes(caminho, Conteudo(0x44));

        var svc = NovoServico();
        var resultado = svc.Move([new QuarantineItem(entrada, "R", "RULE")], PlanoPadrao());

        Assert.Empty(resultado.MovedPaths);
        Assert.Equal([caminho], resultado.SkippedStaleMetadata.ToArray());
        Assert.True(File.Exists(caminho)); // nunca sumiu silenciosamente

        // registro honesto: manifesto existe, sem item fingindo movimento
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(resultado.ManifestPath));
        Assert.Equal(0, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(
            [caminho],
            json.RootElement.GetProperty("skipped_stale_metadata")
                .EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    // ------------------------------------------------------------------
    // ADR-0002 item 4 / ADR-0010 §3: falha no meio ⇒ parcial honesto + exceção
    // ------------------------------------------------------------------
    [Fact]
    public void Move_FalhaNoSegundoItem_ManifestoParcialHonesto_EExcecao()
    {
        // Ordem canônica por caminho (ADR-0003): "dois.txt" < "um.txt" em bytes
        // UTF-8, logo dois move PRIMEIRO mesmo sendo passado depois.
        var um = CriarArquivo("um.txt", Conteudo(0x01));
        var dois = CriarArquivo("dois.txt", Conteudo(0x02));
        var svc = NovoServico(comMoverQueFalhaNo: 2); // 1ª move ok, 2ª explode

        var excecao = Assert.ThrowsAny<Exception>(() => svc.Move(
            [
                new QuarantineItem(Entrada(um), "R", "RULE"),
                new QuarantineItem(Entrada(dois), "R", "RULE"),
            ],
            PlanoPadrao()));

        // primeiro item na ordem canônica: movido E registrado
        Assert.False(File.Exists(dois));
        Assert.IsType<QuarantinePartialException>(excecao);
        var parcial = (QuarantinePartialException)excecao;
        Assert.True(File.Exists(parcial.PartialManifestPath));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(parcial.PartialManifestPath));
        Assert.Equal("partial", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(dois, json.RootElement.GetProperty("items")[0].GetProperty("original_path").GetString());

        // segundo item na ordem: intocado — nada movido ficou sem registro
        Assert.True(File.Exists(um));
    }

    // ------------------------------------------------------------------
    // guarda estática (§22; ADR-0002 item 5; NDES-05): zero delete no módulo
    // ------------------------------------------------------------------
    [Fact]
    public void ModuloQuarentena_ZeroChamadasDelecao_EmDoctorCore()
    {
        var raizCore = RaizFonte("Doctor.Core");
        var violacoes = new List<string>();

        foreach (var cs in Directory.EnumerateFiles(raizCore, "*.cs", SearchOption.AllDirectories))
        {
            if (cs.EndsWith("obj") || cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var numeroLinha = 0;
            foreach (var linha in File.ReadLines(cs))
            {
                numeroLinha++;
                foreach (var padrao in PadroesProibidos)
                {
                    if (Regex.IsMatch(linha, padrao, RegexOptions.IgnoreCase))
                    {
                        violacoes.Add(
                            $"{Path.GetRelativePath(raizCore, cs)}:{numeroLinha} [{padrao}] {linha.Trim()}");
                    }
                }
            }
        }

        Assert.True(violacoes.Count == 0,
            "Guarda anti-delete violada em Doctor.Core — único toque permitido no " +
            $"conteúdo do usuário é File.Move para a quarentena (ADR-0002):\n{string.Join("\n", violacoes)}");
    }

    /// <summary>Validação por mutação: o detector reconhece cada API proibida.</summary>
    [Theory]
    [InlineData("File.Delete(caminho);")]
    [InlineData("Directory.Delete(pasta, recursive: true);")]
    [InlineData("[DllImport(\"kernel32.dll\")] static extern bool DeleteFileW(string p);")]
    [InlineData("SetFileInformationByHandle(h, FileDispositionInfo, &info, 4);")]
    [InlineData("var info = new FILE_DISPOSITION_INFO();")]
    public void DetectorAntiDelete_ReconheceApiProibida_EmTrechoContaminado(string trecho)
    {
        Assert.True(PadroesProibidos.Any(p => Regex.IsMatch(trecho, p, RegexOptions.IgnoreCase)),
            $"Detector não reconheceu o trecho: {trecho}");
    }

    // ==================================================================
    // infraestrutura do teste
    // ==================================================================

    /// <summary>Padrões de deleção permanente proibidos em TODO Doctor.Core.
    /// File.Move é a ÚNICA API destrutiva-permissiva sancionada (ADR-0010 §3).</summary>
    private static readonly string[] PadroesProibidos =
    [
        @"\bFile\.Delete\s*\(",
        @"\bDirectory\.Delete\s*\(",
        @"\bFileSystem\.DeleteFile\s*\(",
        @"\bFileSystem\.DeleteDirectory\s*\(",
        @"\.Delete\s*\(\s*\)",
        @"\bDeleteFileW?\b",
        @"\bRemoveDirectoryW?\b",
        @"\bFILE_DISPOSITION_INFO\b",
        @"\bFileDispositionInfo\b",
    ];

    private static readonly DateTimeOffset TimestampCongelado =
        new(2026, 8, 22, 19, 45, 0, TimeSpan.Zero);

    private static readonly DateTime EntradaFixaMtime =
        new(2026, 8, 20, 10, 30, 0, DateTimeKind.Utc);

    private QuarantineService NovoServico(int? comMoverQueFalhaNo = null)
    {
        var contador = 0;
        return new QuarantineService(
            moveOverride: comMoverQueFalhaNo is null
                ? null
                : (origem, destino) =>
                {
                    contador++;
                    if (contador >= comMoverQueFalhaNo.Value)
                    {
                        throw new IOException($"falha simulada no move #{contador}");
                    }

                    File.Move(origem, destino);
                });
    }

    private QuarantinePlan PlanoPadrao() => new(_root, TimestampCongelado);

    private string PayloadUnico(QuarantineOperationResult movimento)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(movimento.ManifestPath));
        var relativo = json.RootElement.GetProperty("items")[0]
            .GetProperty("quarantine_path").GetString()!;
        return Path.Combine(movimento.QuarantineDirectory, relativo.Replace('/', Path.DirectorySeparatorChar));
    }

    private string RaizFonte(string projeto)
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CloudSyncConflictDoctor.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", projeto);
    }

    private static byte[] Conteudo(byte semente) =>
        Enumerable.Range(0, 4096).Select(i => (byte)(semente + (i % 97))).ToArray();

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
            VolumeId = "t15-volume",
            FileId = caminho,
        };
    }
}
