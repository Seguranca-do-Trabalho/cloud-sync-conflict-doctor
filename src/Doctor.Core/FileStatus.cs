namespace Doctor.Core;

/// <summary>
/// Stability status of a file during the scan (threat-model T-05, rule R4).
/// Unstable files never enter equality/divergence decisions and are never
/// written to the cache — fail-closed: doubt => potential divergence.
/// </summary>
public enum FileStatus
{
    /// <summary>Metadata verified before and after read — stable.</summary>
    Stable,

    /// <summary>Metadata diverged between pre-read and post-read snapshot.
    /// The file was marked as unstable: excluded from equality decisions
    /// and never enters the cache (T-05/R4).</summary>
    Unstable,
}
