namespace Doctor.Core;

/// <summary>
/// Snapshot imutável de metadados capturado ANTES da leitura de conteúdo.
/// Usado para detecção de TOCTOU (threat-model T-05, regra R4):
/// após a leitura, os três campos são relidos e conferidos;
/// qualquer divergência marca o arquivo como UNSTABLE.
/// </summary>
public sealed record MetadataSnapshot(
    long Size,
    long MtimeTicks,
    string FileId);

/// <summary>
/// Resultados da verificação de estabilidade pós-leitura (T-05).
/// </summary>
public sealed record StabilityCheckResult(
    FileStatus Status,
    string? DivergenceReason = null);
