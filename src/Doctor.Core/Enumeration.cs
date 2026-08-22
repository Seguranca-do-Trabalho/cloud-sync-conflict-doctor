namespace Doctor.Core;

using System.Diagnostics;

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
/// marca placeholders via <see cref="PlaceholderPolicy"/> e deriva a telemetria da lista
/// final ordenada. Não lê conteúdo — delega ao enumerador interno apenas metadados.
/// </summary>
public sealed class OrderedFileEnumerator : IFileEnumerator
{
    private readonly IFileEnumerator _inner;

    public OrderedFileEnumerator(IFileEnumerator inner) => _inner = inner;

    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var physical = _inner.Enumerate(rootPath, ct);

        var files = PathOrder.Sort(physical.Files, e => e)
            .Select(e => e with
            {
                IsPlaceholder = PlaceholderPolicy.IsPlaceholder(e),
                PlaceholderKind = PlaceholderPolicy.Classify(e),
            })
            .ToArray();

        var telemetry = physical.Telemetry with
        {
            FilesEnumerated = files.Length,
            FilesPlaceholder = files.Count(f => f.IsPlaceholder),
        };

        return new EnumerationResult(files, physical.Errors, telemetry);
    }
}
