namespace Doctor.Core;

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

/// <summary>Item a mover para a quarentena: snapshot L0 + decisão auditável
/// (SPEC §17/§18; ADR-0002). <c>Reason</c>/<c>Rule</c> entram no manifesto.</summary>
public sealed record QuarantineItem(FileEntry Entry, string Reason, string Rule);

/// <summary>
/// Plano de quarentena (docs/test-strategy.md §2.5): raiz escaneada e timestamp
/// da operação recebidos por injeção — nunca resolvidos globalmente. O timestamp
/// congelado torna o <see cref="QuarantineService.Move"/> testável e determinístico.
/// </summary>
public sealed record QuarantinePlan(string RootPath, DateTimeOffset TimestampUtc);

/// <summary>Resultado de um move completo: caminhos da operação §18.</summary>
public sealed record QuarantineOperationResult(
    string OperationId,
    string QuarantineDirectory,
    string ManifestPath,
    string Status,
    IReadOnlyList<string> MovedPaths,
    IReadOnlyList<string> SkippedStaleMetadata);

/// <summary>Resultado de um restore (§18; ADR-0010 §4).</summary>
public sealed record RestoreResult(
    string OperationId,
    IReadOnlyList<string> RestoredPaths);

/// <summary>Item restaurado com desvio de caminho registrado no histórico.</summary>
public sealed record RestoredItem(
    string OriginalPath,
    string ActualPath,
    bool UsedFallbackSuffix);

/// <summary>
/// Restore abortado: o destino de algum item está ocupado. NADA é sobrescrito
/// (ADR-0002 item 3, ADR-0010 §4) — a exceção carrega o par conflitante.
/// </summary>
public sealed class RestoreConflictException : InvalidOperationException
{
    public RestoreConflictException(string originalPath, string occupiedBy)
        : base($"Restore cancelado: destino ja ocupado, JAMAIS sobrescrever " +
               $"(ADR-0002 item 3 / ADR-0010 4): '{originalPath}' ocupado por '{occupiedBy}'.")
    {
        OriginalPath = originalPath;
        OccupiedBy = occupiedBy;
    }

    /// <summary>Caminho original pretendido para o restore.</summary>
    public string OriginalPath { get; }

    /// <summary>Caminho do payload que não pôde voltar.</summary>
    public string OccupiedBy { get; }
}

/// <summary>
/// Violação de contenção byte-a-byte (threat-model T-01, mitigação (b); regra R2):
/// um caminho de destino de move/resolve/restore não começa pelo prefixo canônico
/// da raiz autorizada. Lançada ANTES de qualquer toque — falha fechada, nada é
/// movido nem escrito.
/// </summary>
public sealed class QuarantineContainmentException : InvalidOperationException
{
    public QuarantineContainmentException(string caminho, string prefixoRaiz)
        : base($"Contenção violada: destino '{caminho}' está fora do prefixo canônico " +
               $"'{prefixoRaiz}'. Operação recusada sem tocar nada (T-01/R2).")
    {
        Caminho = caminho;
        PrefixoRaiz = prefixoRaiz;
    }

    /// <summary>Caminho recusado.</summary>
    public string Caminho { get; }

    /// <summary>Prefixo canônico exigido.</summary>
    public string PrefixoRaiz { get; }
}

/// <summary>
/// Divergência de hash pós-move (threat-model T-04, regra R5): os bytes chegados à
/// quarentena diferem do hash capturado imediatamente antes do move. A fonte foi
/// ROLLBACK-ada (move de volta) e a operação é FALHA — nada é declarado sucesso.
/// O manifesto parcial carrega hash_pre_move ≠ hash_post_move para auditoria.
/// </summary>
public sealed class QuarantineRollbackException : QuarantinePartialException
{
    public QuarantineRollbackException(
        string partialManifestPath,
        string originalPath,
        string hashPreMove,
        string hashPostMove)
        : base(partialManifestPath,
               $"Divergência TOCTOU pós-move em '{originalPath}': hash_pre_move {hashPreMove} " +
               $"!= hash_post_move {hashPostMove}. Rollback executado; operação FALHA (R5).")
    {
        OriginalPath = originalPath;
        HashPreMove = hashPreMove;
        HashPostMove = hashPostMove;
    }

    /// <summary>Caminho original cuja operação sofreu rollback.</summary>
    public string OriginalPath { get; }

    /// <summary>Hash capturado na fonte imediatamente antes do move.</summary>
    public string HashPreMove { get; }

    /// <summary>Hash recalculado no payload já na quarentena.</summary>
    public string HashPostMove { get; }
}

/// <summary>
/// Move falhou no meio da operação: itens já movidos permanecem registrados num
/// manifesto PARCIAL honesto; nada movido fica sem registro (ADR-0002 item 4;
/// ADR-0010 §3). A exceção aponta o manifesto parcial para auditoria.
/// </summary>
public class QuarantinePartialException : InvalidOperationException
{
    public QuarantinePartialException(string partialManifestPath, string message)
        : base($"{message} Manifesto parcial: {partialManifestPath}")
        => PartialManifestPath = partialManifestPath;

    /// <summary>Manifesto com status "partial" refletindo exatamente o que moveu.</summary>
    public string PartialManifestPath { get; }
}

/// <summary>
/// Serviço de quarentena (EPIC 08 / card T15; SPEC §18; ADR-0002 + ADR-0010):
///
/// • ÚNICA API destrutiva-permissiva do produto é <see cref="File.Move"/> para
///   <c>&lt;raiz&gt;/ConflictDoctor/quarantine/&lt;op_id&gt;/payload</c>; nenhum
///   File.Delete/Directory.Delete existe neste módulo (guarda estática QRT).
/// • operation_id determinístico: <c>yyyyMMddTHHmmssZ-&lt;8 hex BLAKE3 truncado do
///   conteúdo do manifesto&gt;</c> — mesmo estado ⇒ mesmo id (ADR-0010 §1).
/// • Ordem canônica: itens ordenados por caminho em bytes UTF-8
///   (<see cref="StringComparer.Ordinal"/>) antes de qualquer movimento.
/// • Revalidação TOCTOU size+mtime contra o snapshot L0 antes de cada move;
///   divergente ⇒ item pulado e registrado (<c>skipped_stale_metadata</c>).
/// • Falha no meio ⇒ manifesto parcial honesto + <see cref="QuarantinePartialException"/>.
/// </summary>
public sealed class QuarantineService
{
    private const int CopyBufferSize = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Injeta um opener customizado (testes); produção usa File.OpenRead.</summary>
    private readonly Func<string, Stream> _openRead;

    /// <summary>Movimentador injetável (testes simulam falha de disco/permissão).</summary>
    private readonly Action<string, string> _move;

    /// <param name="openReadOverride">Fonte de leitura para hash/validação; produção usa File.OpenRead.</param>
    /// <param name="moveOverride">Move primitivo; produção usa File.Move(origem, destino).</param>
    public QuarantineService(
        Func<string, Stream>? openReadOverride = null,
        Action<string, string>? moveOverride = null)
    {
        _openRead = openReadOverride ?? (static path => File.OpenRead(path));
        _move = moveOverride ?? ((origem, destino) => File.Move(origem, destino));
    }

    /// <summary>
    /// Move cada item para <paramref name="plan"/>.RootPath +
    /// ConflictDoctor/quarantine/&lt;op_id&gt;/payload/, grava o manifesto JSON
    /// atomicamente e devolve o resultado. NUNCA apaga: se o move falhar no meio,
    /// o estado fica registrado em manifesto parcial e a exceção propaga
    /// (falha fechada, ADR-0002 item 4).
    /// </summary>
    public QuarantineOperationResult Move(
        IReadOnlyList<QuarantineItem> items,
        QuarantinePlan plan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(plan);

        if (items.Count == 0)
        {
            throw new ArgumentException("Nenhum item para quarentena.", nameof(items));
        }

        ct.ThrowIfCancellationRequested();

        // Ordem canônica por caminho em bytes UTF-8 (ADR-0003 regra 1) — nunca ordem
        // de chegada, nunca filesystem order.
        var ordenados = items
            .OrderBy(i => i.Entry.Path, StringComparer.Ordinal)
            .ToArray();

        var opIdBase = plan.TimestampUtc.ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        // Diretório provisório sem sufixo determinístico ainda (o id depende do
        // manifesto, que depende dos paths finais): cria com nome temporário único
        // baseado NO PLANO, e renomeia ao final. Em duas execuções do mesmo estado
        // com o mesmo plano, o diretório final tem o MESMO nome.
        var quarantineRoot = Path.Combine(
            plan.RootPath, "ConflictDoctor", "quarantine");
        Directory.CreateDirectory(quarantineRoot);

        // Payload staging: os arquivos precisam de destino físico ANTES do id final
        // existir. Usa um diretório de trabalho efêmero dentro da quarentena datada;
        // ao fechar, renomeia para o diretório definitivo <op_id>.
        var stagingName = $"staging-{Guid.NewGuid():N}";
        var stagingDir = Path.Combine(quarantineRoot, stagingName);
        var payloadDir = Path.Combine(stagingDir, "payload");
        Directory.CreateDirectory(payloadDir);

        var registros = new List<Dictionary<string, object?>>();
        var movidos = new List<string>();
        var pulados = new List<string>();
        var mapeamentoPayloads = new Dictionary<string, string>(StringComparer.Ordinal);

        // Prefixo canônico da contenção (T-01/R2): raiz do plano em forma plena,
        // com separador final — todo destino de move/restore DEVE começar por ele.
        var prefixoRaiz = ComSeparadorFinal(Path.GetFullPath(plan.RootPath));

        try
        {
            for (var indice = 0; indice < ordenados.Length; indice++)
            {
                ct.ThrowIfCancellationRequested();
                var item = ordenados[indice];
                var entry = item.Entry;

                // Gate de placeholder (SPEC §6): conteúdo de placeholder NUNCA é lido.
                if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
                {
                    throw new PlaceholderViolationException(
                        entry.Path, "quarentena tentaria ler conteudo de placeholder:");
                }

                // TOCTOU (ADR-0010 §3): revalida size+mtime contra o snapshot L0.
                var info = new FileInfo(entry.Path);
                if (!info.Exists
                    || info.Length != entry.Size
                    || info.LastWriteTimeUtc != entry.MtimeUtc.UtcDateTime)
                {
                    pulados.Add(entry.Path); // arquivo permanece onde está; registro honesto
                    continue;
                }

                // Nome opaco sequencial (ADR-0010 §1): sem relação com o original.
                var nomePayload = $"{indice + 1:D4}.dat";
                var destinoAbsoluto = Path.Combine(payloadDir, nomePayload);

                // Contenção byte-a-byte (threat-model T-01 mitigação (b); regra R2):
                // o destino resolvido DEVE começar pelo prefixo canônico da raiz da
                // operação, por comparação Ordinal sobre a forma plena. Divergência ⇒
                // exceção ANTES de tocar qualquer coisa.
                GarantirContencao(destinoAbsoluto, prefixoRaiz);

                // Hash pré-move do ORIGINAL imediatamente antes do move (T-04/R5),
                // pela mesma cadeia de leitura do gate (_openRead).
                var hashPreMove = FullHashBlake3Streaming(entry.Path, ct);

                _move(entry.Path, destinoAbsoluto);

                movidos.Add(entry.Path);
                mapeamentoPayloads[entry.Path] = $"payload/{nomePayload}";

                // Hash BLAKE3 completo do PAYLOAD já na quarentena (fonte única de
                // verdade do manifesto; streaming, nunca carrega inteiro em memória).
                // É também a VERIFICAÇÃO PÓS-MOVE do R5: divergente do pré-move ⇒
                // rollback (move de volta) e operação FALHA — nada é declarado sucesso.
                var hash = FullHashBlake3Streaming(destinoAbsoluto, ct);
                if (!string.Equals(hash, hashPreMove, StringComparison.Ordinal))
                {
                    RollbackPosMove(
                        stagingDir,
                        payloadDir,
                        nomePayload,
                        entry,
                        item.Reason,
                        item.Rule,
                        hashPreMove,
                        hash);
                    throw new QuarantineRollbackException(
                        Path.Combine(stagingDir, "manifest-partial.json"),
                        entry.Path,
                        hashPreMove,
                        hash);
                }

                registros.Add(new Dictionary<string, object?>
                {
                    ["original_path"] = entry.Path,
                    ["quarantine_path"] = $"payload/{nomePayload}",
                    ["size"] = entry.Size,
                    ["mtime_utc"] = FormatarTimestamp(entry.MtimeUtc),
                    ["hash"] = hash,
                    ["algorithm"] = "BLAKE3",
                    ["hash_version"] = 1,
                    ["reason"] = item.Reason,
                    ["rule"] = item.Rule,
                });
            }

            // Manifesto v1 (ADR-0010 §2): chaves na ordem exata do schema.
            var manifesto = new Dictionary<string, object?>
            {
                ["manifest_version"] = 1,
                ["operation_id"] = null!, // preenchido após derivar o id do conteúdo
                ["created_utc"] = FormatarTimestamp(plan.TimestampUtc),
                ["items"] = registros,
                // status reflete a operação publicada; falhas interrompem antes (parcial honesto)
                ["status"] = "completed",
            };

            // Auditoria TOCTOU (ADR-0010 §3): itens pulados por metadado stale entram
            // no manifesto publicado. Chave presente APENAS quando houver pulados —
            // o caso comum permanece com exatamente as 5 chaves do ADR-0010 §2;
            // mesmo estado ⇒ mesmas chaves ⇒ mesmo id (determinismo).
            if (pulados.Count > 0)
            {
                manifesto["skipped_stale_metadata"] = pulados.ToArray();
            }

            var manifestSemId = Serializar(manifesto, placeholderOpId: true);

            // operation_id = timestamp + 8 hex BLAKE3 do corpo do manifesto SEM o id
            // (auto-referência impossível): mesmo estado ⇒ mesmo corpo ⇒ mesmo id.
            var suffixHex = Convert.ToHexString(Blake3.Hasher.Hash(manifestSemId).AsSpan())
                .ToLowerInvariant()[..8];
            var operationId = $"{opIdBase}-{suffixHex}";
            manifesto["operation_id"] = operationId;

            var manifestFinal = Serializar(manifesto, placeholderOpId: false);

            // Escrita ATÔMICA do manifesto (ADR-0010 §2): .tmp → File.Move overwrite:false.
            var manifestPathFinalStaging = Path.Combine(stagingDir, "manifest.json");
            var tmpPath = manifestPathFinalStaging + ".tmp";
            File.WriteAllBytes(tmpPath, manifestFinal);
            File.Move(tmpPath, manifestPathFinalStaging, overwrite: false);

            // Publica o diretório definitivo <op_id>: renomeia staging → final.
            var finalDir = Path.Combine(quarantineRoot, operationId);
            Directory.Move(stagingDir, finalDir);

            return new QuarantineOperationResult(
                operationId,
                finalDir,
                Path.Combine(finalDir, "manifest.json"),
                movidos.Count > 0 || pulados.Count > 0 ? "completed" : "completed",
                movidos.ToArray(),
                pulados.ToArray());
        }
        catch (Exception excecao) when (excecao is not QuarantinePartialException
                                      && excecao is not PlaceholderViolationException
                                            || true)
        {
            // Falha fechada (ADR-0002 item 4): registra o estado real num manifesto
            // PARCIAL honesto dentro do staging e propaga. Nada movido fica sem
            // registro; nada parcial permanece sem rastro.
            var parcialPath = Path.Combine(stagingDir, "manifest-partial.json");
            try
            {
                var parcial = new Dictionary<string, object?>
                {
                    ["manifest_version"] = 1,
                    ["operation_id"] = "(parcial)",
                    ["created_utc"] = FormatarTimestamp(plan.TimestampUtc),
                    ["items"] = registros,
                    ["status"] = "partial",
                    ["skipped_stale_metadata"] = pulados.ToArray(),
                };

                if (!File.Exists(parcialPath))
                {
                    File.WriteAllBytes(parcialPath, SerializarParcial(parcial));
                }
            }
            catch
            {
                // nem o registro parcial foi possível: propaga a falha original —
                // nunca mascara um erro com outro.
            }

            throw excecao is QuarantinePartialException
                ? excecao
                : new QuarantinePartialException(parcialPath, Mensagem(excecao));
        }
        finally
        {
            // staging remanescente (falha) NÃO é apagado — preserva evidência.
            if (Directory.Exists(stagingDir))
            {
                // mantém: auditoria do ADR-0002 exige que nada desapareça silenciosamente
            }
        }
    }

    /// <summary>
    /// Restaura todos os itens da operação para seus <c>original_path</c>.
    /// PRÉ-CHECAGEM TOTAL antes do primeiro toque: se QUALQUER destino estiver
    /// ocupado, lança <see cref="RestoreConflictException"/> sem tocar em NADA
    /// (nem ocupante, nem payload, nem manifesto). Nunca sobrescreve (ADR-0002
    /// item 3). Valida o hash BLAKE3 de cada payload antes de mover de volta.
    /// </summary>
    public RestoreResult Restore(string operationId, string rootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var dirOperacao = Path.Combine(rootPath, "ConflictDoctor", "quarantine", operationId);
        var manifestPath = Path.Combine(dirOperacao, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Manifesto da operação não encontrado: {manifestPath}", manifestPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var raiz = doc.RootElement;

        var status = raiz.GetProperty("status").GetString();
        if (status == "partial")
        {
            throw new InvalidOperationException(
                $"Operação {operationId} está PARTIAL — restore manual exigido; " +
                "não há como restaurar automaticamente itens nunca movidos.");
        }

        var itens = raiz.GetProperty("items");
        if (itens.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"Operação {operationId} não tem itens restauráveis.");
        }

        // ---- fase 1: validar TUDO (hash + destinos livres) sem tocar nada --------
        var pendentes = new List<(string PayloadAbsoluto, string Destino, string HashEsperado)>();

        // Contenção byte-a-byte (threat-model T-01 mitigação (b); regra R2): o
        // destino de CADA item deve começar pelo prefixo canônico da raiz informada
        // — manifesto forjado ou corrompido com original_path externo é recusado
        // ANTES de qualquer toque, sem mover nem escrever nada.
        var prefixoRaiz = ComSeparadorFinal(Path.GetFullPath(rootPath));

        foreach (var item in itens.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var relativo = item.GetProperty("quarantine_path").GetString()!;
            var payloadAbsoluto = Path.Combine(dirOperacao, relativo.Replace('/', Path.DirectorySeparatorChar));
            var destino = item.GetProperty("original_path").GetString()!;
            var hashEsperado = item.GetProperty("hash").GetString()!;

            // contenção ANTES de qualquer validação com I/O sobre o item
            GarantirContencao(destino, prefixoRaiz);

            // validação de integridade: payload corrompido ⇒ restore recusado.
            var hashAtual = FullHashBlake3Streaming(payloadAbsoluto, ct);
            if (!string.Equals(hashAtual, hashEsperado, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Integridade FALHOU para '{payloadAbsoluto}': hash atual {hashAtual} " +
                    $"!= manifesto {hashEsperado}. Restore recusado.");
            }

            if (File.Exists(destino) || Directory.Exists(destino))
            {
                throw new RestoreConflictException(destino, payloadAbsoluto);
            }

            pendentes.Add((payloadAbsoluto, destino, hashEsperado));
        }

        // ---- fase 2: mover de volta, recriando diretórios pais ausentes ----------
        var restaurados = new List<string>();
        foreach (var (payloadAbsoluto, destino, _) in pendentes)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            File.Move(payloadAbsoluto, destino, overwrite: false);
            restaurados.Add(destino);
        }

        // ---- fase 3: marcar restored no manifesto (histórico nunca apagado) -----
        MarcarRestored(manifestPath, pendentes.Select(p => p.Destino).ToArray(), operationId);

        return new RestoreResult(operationId, restaurados.ToArray());
    }

    // ------------------------------------------------------------------
    // infraestrutura interna
    // ------------------------------------------------------------------

    /// <summary>
    /// Contenção byte-a-byte (threat-model T-01 mitigação (b); regra R2):
    /// <paramref name="caminho"/> resolvido à forma plena DEVE começar pelo
    /// prefixo canônico de <paramref name="prefixoRaiz"/> em comparação Ordinal.
    /// Qualquer fuga — traversal, symlink resolvido fora, manifesto forjado —
    /// lança <see cref="QuarantineContainmentException"/> sem tocar nada.
    /// </summary>
    private static void GarantirContencao(string caminho, string prefixoRaiz)
    {
        var pleno = Path.GetFullPath(caminho);

        if (!pleno.StartsWith(prefixoRaiz, StringComparison.Ordinal))
        {
            throw new QuarantineContainmentException(caminho, prefixoRaiz);
        }
    }

    /// <summary>Prefixo canônico com separador final: evita que "/raiz-evil"
    /// passe pela contenção de "/raiz" (comparação de componente inteiro).</summary>
    private static string ComSeparadorFinal(string diretorio) =>
        diretorio.EndsWith(Path.DirectorySeparatorChar)
            ? diretorio
            : diretorio + Path.DirectorySeparatorChar;

    /// <summary>
    /// Rollback do R5: move o payload divergente DE VOLTA ao caminho original,
    /// remove o registro do item movido e grava o manifesto parcial com status
    /// "failed" e a evidência auditable hash_pre_move ≠ hash_post_move. Falha no
    /// próprio rollback propaga a exceção original — nunca mascara um erro com outro.
    /// </summary>
    private void RollbackPosMove(
        string stagingDir,
        string payloadDir,
        string nomePayload,
        FileEntry entry,
        string reason,
        string rule,
        string hashPreMove,
        string hashPostMove)
    {
        var payloadAbsoluto = Path.Combine(payloadDir, nomePayload);

        try
        {
            if (File.Exists(payloadAbsoluto) && !File.Exists(entry.Path))
            {
                _move(payloadAbsoluto, entry.Path);
            }
        }
        finally
        {
            // evidência auditable mesmo se o rollback físico falhar (R11):
            // registro FALHA com a cadeia hash_pre_move/hash_post_move.
            try
            {
                var parcial = new Dictionary<string, object?>
                {
                    ["manifest_version"] = 1,
                    ["operation_id"] = "(parcial)",
                    ["created_utc"] = FormatarTimestamp(DateTimeOffset.UtcNow),
                    ["items"] = new List<Dictionary<string, object?>>
                    {
                        new()
                        {
                            ["original_path"] = entry.Path,
                            ["quarantine_path"] = $"payload/{nomePayload}",
                            ["size"] = entry.Size,
                            ["mtime_utc"] = FormatarTimestamp(entry.MtimeUtc),
                            ["hash_pre_move"] = hashPreMove,
                            ["hash_post_move"] = hashPostMove,
                            ["algorithm"] = "BLAKE3",
                            ["hash_version"] = 1,
                            ["reason"] = reason,
                            ["rule"] = rule,
                        },
                    },
                    ["status"] = "failed",
                };

                var parcialPath = Path.Combine(stagingDir, "manifest-partial.json");
                if (!File.Exists(parcialPath))
                {
                    File.WriteAllBytes(parcialPath, SerializarParcial(parcial));
                }
            }
            catch
            {
                // nem o registro foi possível: a exceção do rollback/origem prevalece
            }
        }
    }

    private string FullHashBlake3Streaming(string caminho, CancellationToken ct)
    {
        using var stream = _openRead(caminho);
        using var hasher = Blake3.Hasher.New();

        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = stream.Read(rented, 0, rented.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Update(rented.AsSpan(0, read));
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }

        var hashBytes = hasher.Finalize();
        return Convert.ToHexString(hashBytes.AsSpan()).ToLowerInvariant(); // hex minúscula (ADR-0005 §1)
    }

    private static string FormatarTimestamp(DateTimeOffset ts) =>
        ts.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static byte[] Serializar(Dictionary<string, object?> manifesto, bool placeholderOpId)
    {
        if (placeholderOpId)
        {
            // Corpo DERIVÁVEL: serializa com operation_id substituído por marcador
            // fixo — o id deriva do conteúdo real sem auto-referência.
            var copia = new Dictionary<string, object?>(manifesto)
            {
                ["operation_id"] = "<op_id>",
            };
            return JsonSerializer.SerializeToUtf8Bytes(copia, JsonOptions);
        }

        return JsonSerializer.SerializeToUtf8Bytes(manifesto, JsonOptions);
    }

    private static byte[] SerializarParcial(Dictionary<string, object?> parcial) =>
        JsonSerializer.SerializeToUtf8Bytes(parcial, JsonOptions);

    private void MarcarRestored(string manifestPath, string[] destinos, string operationId)
    {
        // Lê, marca item.status=restored, reescreve ATOMICAMENTE (.tmp → Move).
        // Campos originais preservados; histórico nunca apagado (ADR-0010 §4.4).
        var bytes = File.ReadAllBytes(manifestPath);
        using var doc = JsonDocument.Parse(bytes);
        var raiz = doc.RootElement;

        var manifestoReescrito = new Dictionary<string, object?>();
        foreach (var prop in raiz.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "items":
                    var itens = new List<Dictionary<string, object?>>();
                    var idx = 0;
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        var dict = JsonElementParaDict(item);
                        // ADR-0010 §4.4: status do ITEM → "restored"; desvio de caminho
                        // registrado ao lado; histórico original preservado.
                        dict["status"] = "restored";
                        dict["restored_to"] = destinos[idx];
                        idx++;
                        itens.Add(dict);
                    }

                    manifestoReescrito["items"] = itens;
                    break;
                case "status":
                    manifestoReescrito["status"] = "restored";
                    break;
                default:
                    // JsonElement.Clone() preserva ValueKind (números continuam números).
                    manifestoReescrito[prop.Name] = prop.Value.Clone();
                    break;
            }
        }

        var tmp = manifestPath + ".restore.tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(manifestoReescrito, JsonOptions));
        // Reescrita ATÔMICA do PRÓPRIO manifesto da operação (ADR-0010 §4.4 —
        // "reescrevendo o manifesto atomicamente; histórico nunca é apagado"):
        // o alvo é metadado interno nosso, nunca conteúdo do usuário — o destino
        // de usuário só recebe payload após verificação de que está LIVRE.
        File.Move(tmp, manifestPath, overwrite: true);
    }

    private static Dictionary<string, object?> JsonElementParaDict(JsonElement elemento)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in elemento.EnumerateObject())
        {
            // Clone() preserva o ValueKind: size continua número, strings continuam
            // strings — o manifesto reescrito mantém os tipos do schema v1.
            dict[prop.Name] = prop.Value.Clone();
        }

        return dict;
    }

    private static string Mensagem(Exception excecao) =>
        $"Move interrompido: {excecao.GetType().Name}: {excecao.Message}";
}
