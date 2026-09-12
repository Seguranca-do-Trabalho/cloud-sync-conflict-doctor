namespace Doctor.Core;

/// <summary>
/// Immutable metadata snapshot captured BEFORE content reading.
/// Used for TOCTOU detection (threat-model T-05, rule R4):
/// after reading, the three fields are re-read and verified;
/// any divergence marks the file as UNSTABLE.
/// </summary>
public sealed record MetadataSnapshot(
    long Size,
    long MtimeTicks,
    string FileId);

/// <summary>
/// Post-read stability check results (T-05).
/// </summary>
public sealed record StabilityCheckResult(
    FileStatus Status,
    string? DivergenceReason = null);
