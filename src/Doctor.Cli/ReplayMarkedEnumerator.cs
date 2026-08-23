namespace Doctor.Cli;

using Doctor.Core;

/// <summary>
/// T16 — replay da lista L0 já marcada e ordenada para o pipeline. O ScanPipeline
/// reordena via OrderedFileEnumerator, cuja projeção consulta PlaceholderPolicy;
/// com o endurecimento do Classify (marcação da origem é autoridade máxima), a
/// marca da convenção T04 de sidecar sobrevive a qualquer recomposição — inclusive
/// a este replay. Não faz I/O; nunca altera Path, Size, MtimeUtc nem marcação.
/// </summary>
public sealed class ReplayMarkedEnumerator : IFileEnumerator
{
    private readonly IReadOnlyList<FileEntry> _arquivos;

    public ReplayMarkedEnumerator(IReadOnlyList<FileEntry> arquivos) => _arquivos = arquivos;

    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) =>
        new(_arquivos, Array.Empty<ScanError>(), new ScanTelemetry());
}
