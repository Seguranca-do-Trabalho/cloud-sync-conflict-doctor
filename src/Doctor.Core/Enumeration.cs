namespace Doctor.Core;

using System.Diagnostics;

// ScanTelemetry mudou de lugar: fonte única em Telemetry.cs (contrato do T06,
// SPEC §10 completo + gate placeholder_bytes_read). Este arquivo mantém apenas
// os tipos de enumeração Level 0.
/// <summary>
/// Erro individual de scan (permissão, caminho longo, etc.). Nunca aborta o scan:
/// vira registro no relatório — sem falha silenciosa (docs/contratos.md, risco R10).
/// </summary>
[DebuggerDisplay("{" + nameof(ToString) + "(),nq}")]
public sealed record ScanError(string Path, string Message);

/// <summary>
/// Resultado da enumeração Level 0 (docs/contratos.md): <see cref="Files"/> vem SEMPRE
/// ordenado pela ordem canônica (<see cref="PathOrder"/>) — independente da ordem física
/// do filesystem; erros individuais não abortam o scan.
/// </summary>
public sealed record EnumerationResult(
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<ScanError> Errors,
    ScanTelemetry Telemetry);

/// <summary>
/// Contrato de enumeração Level 0 — somente metadados, zero leitura de conteúdo
/// (SPEC §5; docs/contratos.md). Implementadores podem varrer em qualquer ordem física;
/// nunca atravessam reparse points de diretório; nunca abrem placeholder (SPEC §6).
/// </summary>
public interface IFileEnumerator
{
    EnumerationResult Enumerate(string rootPath, CancellationToken ct = default);
}

/// <summary>
/// Wrapper determinístico sobre um enumerador físico qualquer (SPEC §3):
/// reordena pela ordem canônica byte-a-byte de caminho (<see cref="PathOrder"/>),
/// marca placeholders via <see cref="PlaceholderPolicy"/>, aplica a <see cref="ReparsePolicy"/>
/// (marcação IsReparsePoint, guarda de visitados por inode e teto de profundidade —
/// threat-model T-02) e deriva a telemetria da lista final ordenada.
/// Não lê conteúdo — delega ao enumerador interno apenas metadados.
/// Rejeições da policy viram <see cref="ScanError"/> em <see cref="EnumerationResult.Errors"/>:
/// nunca falha silenciosa (contratos.md R10).
/// </summary>
public sealed class OrderedFileEnumerator : IFileEnumerator
{
    private readonly IFileEnumerator _inner;
    private readonly ReparsePolicy _reparsePolicy;

    public OrderedFileEnumerator(IFileEnumerator inner, ReparsePolicy? reparsePolicy = null)
    {
        _inner = inner;
        _reparsePolicy = reparsePolicy ?? new ReparsePolicy();
    }

    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var physical = _inner.Enumerate(rootPath, ct);

        // ---- ordenação canônica + marcações (ordem física nunca decide; §3) -------------
        var ordenados = PathOrder.Sort(physical.Files, e => e)
            .Select(e => e with
            {
                IsPlaceholder = PlaceholderPolicy.IsPlaceholder(e),
                PlaceholderKind = PlaceholderPolicy.Classify(e),
                IsReparsePoint = e.IsReparsePoint || (e.Attributes & FileAttributes.ReparsePoint) != 0,
            })
            .ToArray();

        // ---- ReparsePolicy: loop detection por inode + teto de profundidade --------------
        // Guarda de visitados por (VolumeId, FileId) sobre a lista JÁ ordenada: decisão
        // determinística — fica sempre a PRIMEIRA ocorrência na ordem canônica, qualquer
        // que seja a ordem física de chegada (mitigação T-02; contratos.md §3).
        var visitados = new HashSet<(string VolumeId, string FileId)>();
        var files = new List<FileEntry>(ordenados.Length);
        var errors = new List<ScanError>(physical.Errors);
        var raizNormalizada = rootPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

        // ---- SEG-12 (adendo T-15; caso T-06/R1): subárvore reservada fora da enumeração --
        // <raiz>/ConflictDoctor/ é território da ferramenta (quarentena §18/SPEC,
        // ADR-0002): qualquer entrada sob esse prefixo é política estrutural da
        // fronteira canônica Level 0 — nunca candidato, nunca erro (contratos.md R10),
        // nunca contagem em files_enumerated. Comparação de PREFIXO EM BYTES sobre o
        // caminho canônico (Ordinal), separador já normalizado dos dois lados.
        var prefixoReservado = raizNormalizada
            + Path.DirectorySeparatorChar
            + SubarvoreReservada.NomeDiretorio
            + Path.DirectorySeparatorChar;

        var excluidosReservados = 0;

        foreach (var entry in ordenados)
        {
            var caminhoCanônico = entry.Path.Replace(
                Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            if (caminhoCanônico.StartsWith(prefixoReservado, StringComparison.Ordinal))
            {
                excluidosReservados++;
                continue;
            }

            if (!visitados.Add((entry.VolumeId, entry.FileId)))
            {
                errors.Add(new ScanError(
                    entry.Path,
                    "reparse: caminho volta ao mesmo inode ja visitado - entrada rejeitada (loop detection, threat-model T-02)"));
                continue;
            }

            var depth = Profundidade(entry.Path, raizNormalizada);
            if (depth > _reparsePolicy.MaxDepth)
            {
                errors.Add(new ScanError(
                    entry.Path,
                    $"reparse: profundidade {depth} excede o teto de {_reparsePolicy.MaxDepth} - entrada rejeitada (threat-model T-02)"));
                continue;
            }

            files.Add(entry);
        }

        var telemetry = physical.Telemetry with
        {
            FilesEnumerated = files.Count,
            FilesPlaceholder = files.Count(f => f.IsPlaceholder),
            // Cumulativo entre camadas: a composição de produção sofre wrap duplo
            // (pipeline → Ordered sobre Ordered+CrossPlatform) e a camada interna já
            // excluiu; recontar zeraria o contador (idempotência §20).
            FilesExcludedConflictDoctor =
                physical.Telemetry.FilesExcludedConflictDoctor + excluidosReservados,
        };

        return new EnumerationResult(files, errors, telemetry);
    }

    /// <summary>Profundidade relativa à raiz em segmentos de diretório (raiz = 0).</summary>
    private static int Profundidade(string path, string raizNormalizada)
    {
        var normalizado = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (!normalizado.StartsWith(raizNormalizada, StringComparison.Ordinal))
        {
            return 0; // caminho fora da raiz não tem profundidade relativa mensurável
        }

        var relativo = normalizado.AsSpan(raizNormalizada.Length).TrimStart(Path.DirectorySeparatorChar);
        if (relativo.IsEmpty)
        {
            return 0;
        }

        var profundidade = 1; // o primeiro segmento após a raiz é o próprio arquivo
        foreach (var c in relativo)
        {
            if (c == Path.DirectorySeparatorChar)
            {
                profundidade++;
            }
        }

        return profundidade;
    }
}
