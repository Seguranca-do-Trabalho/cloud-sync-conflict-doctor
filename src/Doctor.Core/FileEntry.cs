namespace Doctor.Core;

/// <summary>
/// Metadata collected at Level 0 (SPEC §5). Immutable after creation.
/// Contract: docs/contracts.md. VolumeId/FileId feed the cache logical key (SPEC §12).
/// </summary>
public sealed record FileEntry
{
    /// <summary>Full normalized path.</summary>
    public required string Path { get; init; }

    public required long Size { get; init; }

    public required DateTimeOffset MtimeUtc { get; init; }

    public required System.IO.FileAttributes Attributes { get; init; }

    /// <summary>Stable volume identifier (source of the (volume_id, file_id) pair in the cache).</summary>
    public required string VolumeId { get; init; }

    /// <summary>NTFS file ID / equivalent inode — never the path (SPEC §12).</summary>
    public required string FileId { get; init; }

    /// <summary>True if the content can NEVER be opened (SPEC §6). Marked by the ordered enumerator.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>Reason for the placeholder marking; null when not a placeholder.</summary>
    public PlaceholderKind? PlaceholderKind { get; init; }

    /// <summary>
    /// True if the entry carries a reparse point (junction, symlink, mount point —
    /// POSIX analogue included). Marked by the ordered enumerator via <see cref="ReparsePolicy"/>;
    /// origin marking (physical enumerator) is the highest authority and is never erased.
    /// </summary>
    public bool IsReparsePoint { get; init; }

    /// <summary>
    /// Structural marker for bidi control characters in the path (D5 of card
    /// S11-1; T-01/R12). The NAME is never mutated to "fix" anything: the flag exposes
    /// the risk and the rendering/escaping is left to EPIC 10 (R12). Always derived from
    /// the <see cref="Path"/> itself via <see cref="PathCanonical.HasBidiControlChars"/> —
    /// computed property, impossible to desynchronize from the name.
    /// </summary>
    public bool HasBidiControlChars => PathCanonical.HasBidiControlChars(Path);

    /// <summary>
    /// File stability status (threat-model T-05, rule R4).
    /// Stable = metadata verified before and after read; Unstable = divergence detected,
    /// excluded from equality decisions and from the cache.
    /// </summary>
    public FileStatus Status { get; init; } = FileStatus.Stable;
}
