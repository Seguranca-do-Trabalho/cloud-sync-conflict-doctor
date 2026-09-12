namespace Doctor.Core;

using System.IO.Enumeration;

/// <summary>
/// Level 0 cross-platform enumeration (SPEC §5): recursive scan that brings
/// metadata in the directory entry itself — no extra stat call per file.
/// Never reads content. NEVER crosses directory reparse points (junction,
/// symlink, mount point — ADR-0004 rule 4, threat-model T-02, PLH-04): the
/// directory with reparse is a LEAF, recorded in <see cref="EnumerationResult.Errors"/>
/// and the scan continues. FILE symlinks enter as entries marked
/// ReparsePoint (POSIX analogue of reparse point — SPEC §6). Placeholder
/// marking is exclusively via <see cref="PlaceholderPolicy"/>.
/// Physical order is whatever the filesystem delivers: canonical ordering is
/// the exclusive responsibility of <see cref="OrderedFileEnumerator"/>.
///
/// Implementation note: uses <see cref="FileSystemEnumerator{TResult}"/> directly
/// (not FileSystemEnumerable+options) because only the ShouldRecurseIntoEntry
/// override guarantees non-descent into reparse: with AttributesToSkip=0 (required
/// for FILE symlinks to appear marked), .NET's default behavior recurses into
/// directory symlinks following the target and enters cycles — observed in T09
/// (84 entries in a cycle a→b→a; external content outside root leaked into the list).
/// </summary>
public sealed class CrossPlatformEnumerator : IFileEnumerator
{
    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
    {
        var files = new List<FileEntry>();
        var errors = new List<ScanError>();

        using var enumerator = new NativeEnumerator(rootPath, errors);
        while (enumerator.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            if (enumerator.Current is { } item)
            {
                files.Add(item);
            }
        }

        // Reparse leaves registered during descent + individual access failures:
        // error never aborts the scan (contracts.md R10).
        errors.AddRange(enumerator.AccessErrors);

        // Telemetry derived from the final list (never from incremental counters
        // dependent on physical order) — same contract as OrderedFileEnumerator.
        var telemetry = new ScanTelemetry
        {
            FilesEnumerated = files.Count,
            FilesPlaceholder = files.Count(f => f.IsPlaceholder),
        };

        return new EnumerationResult(files, errors, telemetry);
    }

    /// <summary>
    /// Physical scan: recursion blocked at any directory with the reparse bit
    /// (centralized decision in <see cref="PlaceholderPolicy"/> bits) — the cycle
    /// is never started because descent is refused at the directory entry.
    /// </summary>
    private sealed class NativeEnumerator : FileSystemEnumerator<FileEntry?>
    {
        private readonly List<ScanError> _accessErrors;

        public NativeEnumerator(string root, List<ScanError> accessErrors)
            : base(root, options: new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0, // nothing filtered: placeholders are MARKED, not hidden
            })
            => _accessErrors = accessErrors;

        public List<ScanError> AccessErrors { get; } = new();

        /// <summary>
        /// ADR-0004 rule 4: directory with reparse point is ALWAYS a leaf —
        /// registered and never descended into. Applies to junction, directory
        /// symlink, mount point, any target.
        /// </summary>
        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            // Centralized decision: the "do not touch" bits live in PlaceholderPolicy.
            if (PlaceholderPolicy.IsPlaceholder(entry.Attributes))
            {
                _accessErrors.Add(new ScanError(
                    entry.ToFullPath(),
                    "directory reparse point (junction/symlink) not crossed — leaf registered (ADR-0004 rule 4; threat-model T-02)"));
                return false;
            }

            return true;
        }

        protected override FileEntry? TransformEntry(ref FileSystemEntry entry)
        {
            try
            {
                if (entry.IsDirectory)
                {
                    return null; // only files in Level 0 list (directory reparse already became a registered leaf)
                }

                // File identity, decided at RUNTIME.
                //
                // Previously this was a `#if WINDOWS`, and both branches were wrong
                // on Windows:
                //
                //   - the compiled branch (`#else`) called LinuxFileId.GetInode(),
                //     a P/Invoke of lstat(2). On Windows it threw
                //     DllNotFoundException('libc'); the catch below swallowed the error
                //     and DISCARDED the file — the enumeration returned zero
                //     files in a populated tree.
                //   - the intended branch (`#if WINDOWS`) returned "0" for ALL
                //     files. Since OrderedFileEnumerator uses (VolumeId,
                //     FileId) to detect cycles, the first file entered and
                //     all others were rejected as "same inode".
                //
                // The WINDOWS symbol was never defined (TFM net8.0), so the
                // first branch was what applied — on both platforms.
                // The decision is now by OperatingSystem, which doesn't depend on
                // compilation symbols.
                string fileId = OperatingSystem.IsWindows()
                    ? PathIdentity(entry.ToFullPath())
                    : LinuxFileId.GetInode(entry.ToFullPath());

                // Placeholder marking AT THE ORIGIN (SPEC §6, defense in depth):
                // this enumerator is usable raw (tests, harness); consumers using
                // OrderedFileEnumerator get the policy reapplied (idempotent).
                var built = new FileEntry
                {
                    Path = entry.ToFullPath(),
                    Size = entry.Length,
                    MtimeUtc = entry.LastWriteTimeUtc,
                    Attributes = entry.Attributes,
                    VolumeId = Environment.MachineName, // stable local identification; NTFS volume GUID comes with the native enumerator
                    FileId = fileId,
                };
                return built with
                {
                    IsPlaceholder = PlaceholderPolicy.IsPlaceholder(built),
                    PlaceholderKind = PlaceholderPolicy.Classify(built),
                };
            }
            catch (Exception ex)
            {
                // Individual error never aborts the scan — recorded by caller via Errors.
                AccessErrors.Add(new ScanError(entry.ToFullPath(), ex.Message));
                return null;
            }
        }

        /// <summary>
        /// Identity derived from canonical path, for Windows.
        ///
        /// This enumerator cannot obtain the NTFS FileId without opening a handle
        /// per file, which would violate the SPEC §5 I/O budget. In
        /// production Windows uses <c>WindowsNativeEnumerator</c>, which gets the
        /// 128-bit FileId from the enumeration itself, with no extra cost.
        ///
        /// Here the identity is the normalized path: ensures distinct files
        /// have distinct keys — which suffices for <c>OrderedFileEnumerator</c>
        /// deduplication not to collapse the list. The honest limitation
        /// is that two hard links to the same inode appear as
        /// separate entries; cycle detection via reparse point is still
        /// covered by ADR-0004 rule 4, which treats a directory with reparse
        /// as a leaf and never descends into it.
        /// </summary>
        private static string PathIdentity(string path)
        {
            var canonical = path
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .ToLowerInvariant();   // NTFS is case-insensitive
            return "path:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }
}
