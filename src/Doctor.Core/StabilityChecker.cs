namespace Doctor.Core;

/// <summary>
/// File stability checker (threat-model T-05, rule R4).
///
/// Protocol:
/// 1. Capture metadata snapshot BEFORE reading (size, mtime ticks, file_id)
/// 2. Read content via IStreamSource
/// 3. Re-read metadata AFTER reading
/// 4. Compare: divergence => UNSTABLE, stable => Stable
///
/// Fail-closed: any divergence => doubt about integrity => treat as
/// potentially unstable. Unstable file hash is NEVER written to the cache.
/// </summary>
public static class StabilityChecker
{
    /// <summary>
    /// Verifies that file metadata remained stable between the pre-read
    /// snapshot and the post-read state.
    /// </summary>
    /// <param name="entry">Entry with current metadata (post-read).</param>
    /// <param name="preSnapshot">Snapshot captured before reading.</param>
    /// <param name="postSnapshot">Snapshot captured after reading.</param>
    /// <returns>Stability check result.</returns>
    public static StabilityCheckResult Check(
        FileEntry entry,
        MetadataSnapshot preSnapshot,
        MetadataSnapshot postSnapshot)
    {
        // Size check
        if (postSnapshot.Size != preSnapshot.Size)
        {
            return new StabilityCheckResult(
                FileStatus.Unstable,
                $"size diverged: pre={preSnapshot.Size}, post={postSnapshot.Size}");
        }

        // Mtime check (ticks)
        if (postSnapshot.MtimeTicks != preSnapshot.MtimeTicks)
        {
            return new StabilityCheckResult(
                FileStatus.Unstable,
                $"mtime diverged: pre={preSnapshot.MtimeTicks}, post={postSnapshot.MtimeTicks}");
        }

        // File_id check
        if (postSnapshot.FileId != preSnapshot.FileId)
        {
            return new StabilityCheckResult(
                FileStatus.Unstable,
                $"file_id diverged: pre={preSnapshot.FileId}, post={postSnapshot.FileId}");
        }

        return new StabilityCheckResult(FileStatus.Stable);
    }

    /// <summary>
    /// Creates a metadata snapshot from FileInfo (production).
    /// </summary>
    public static MetadataSnapshot CaptureFromFileInfo(FileInfo info) =>
        new(info.Length, new DateTimeOffset(info.LastWriteTimeUtc).Ticks, info.FullName);

    /// <summary>
    /// Creates a snapshot from FileEntry (already has size/mtime/file_id).
    /// </summary>
    public static MetadataSnapshot CaptureFromEntry(FileEntry entry) =>
        new(entry.Size, entry.MtimeUtc.Ticks, entry.FileId);
}
