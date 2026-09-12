namespace Doctor.Core;

using System.Text;

/// <summary>
/// UNIQUE module for canonization and containment of ABSOLUTE paths at the
/// filesystem boundary (card S11-1/t_17f56008; threat-model T-01 mitigations (a)/(b)/(c);
/// rules R2/R12; SPEC §7–§9; ADR-0002 fail-closed; decisions D4/D5/D6 of the card).
/// Complementary to <see cref="CanonicalPath"/> (T17 RELATIVE name gate):
/// that approves names before they exist on the volume; this governs paths that the
/// operating system has already materialized or will materialize.
///
/// Contract:
/// (a) <see cref="CanonicalizeRoot"/> canonizes the root ONCE at entry —
///     full resolution via GetFullPath and conversion to \\?\ extended form —
///     and it is THIS string, byte-exact, that feeds all containment comparisons.
///     <see cref="ToExtendedLength"/> disables on Windows the Win32 resolution that truncates
///     paths above 260 chars and discards trailing dot/space: the two vectors
///     of T-01. On POSIX it is declared identity (there is no limit nor rewriting).
/// (b) <see cref="Combine"/> combines paths STRUCTURALLY with re-canonicalization
///     at each step — raw string concatenation is forbidden in the product. An injected
///     absolute segment is not silently sanitized: re-canonicalization makes the
///     deviation EXPLICIT in the product and containment rejects it (fail-closed — never
///     "fixing").
/// (c) <see cref="EnsureContained"/> checks BYTE-BYTE containment (D4:
///     StringComparison.Ordinal over UTF-16 code units — no Unicode/locale fold
///     in a path decision, consistent with <see cref="PathOrder"/>'s
///     StringComparer.Ordinal) of the resolved path against the canonical root prefix,
///     BEFORE every write/move operation. Outside root ⇒
///     <see cref="PathEscapeException"/> — fail-closed, operation aborted without touching anything.
///
/// Names are preserved EXACTLY as the filesystem gives them (T-01 mitigation (c)):
/// nothing here fixes trailing dot/space, reserved names or case. Detection of bidi
/// control characters for the REPORT is <see cref="HasBidiControlChars"/> — structural
/// marker (D5); rendering/escaping is left to EPIC 10 (R12). Grouping by
/// normalized_base_name remains in exact UTF-8 bytes (SPEC §7): homoglyph is a DIFFERENT
/// name and is never merged here.
///
/// Mandatory consumers (D6): quarantine move/restore today; EPICs 03/07/08
/// consume this same primitive with their own integration tests.
/// </summary>
public static class PathCanonical
{
    /// <summary>Win32 extended device prefix.</summary>
    private const string ExtendedPrefix = @"\??\";

    /// <summary>Extended UNC prefix.</summary>
    private const string ExtendedUncPrefix = @"\??\UNC\";

    /// <summary>
    /// Bidirectional Unicode control characters (R12/D5): embedded in a name,
    /// they reorder RENDERING ("fdp\u202Eexe.pdf" displays "exe.pdf") without altering
    /// the stored bytes. Fixed and auditable list — U+202A–U+202E (embedding/overrides),
    /// U+2066–U+2069 (isolates), U+200E/U+200F (LRM/RLM marks) and U+061C (Arabic).
    /// </summary>
    private static readonly ReadOnlyMemory<char> BidiControls = new[]
    {
        '\u202A', '\u202B', '\u202C', '\u202D', '\u202E',
        '\u2066', '\u2067', '\u2068', '\u2069',
        '\u200E', '\u200F',
        '\u061C',
    };

    /// <summary>
    /// Canonical form of the ROOT: absolute, resolved and converted to extended form.
    /// Canonicalize ONCE at entry; reuse the byte-exact return in all containment checks.
    /// </summary>
    public static string CanonicalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return ToExtendedLength(Path.GetFullPath(root));
    }

    /// <summary>
    /// Conversion to \\?\ extended form (idempotent). On Windows disables the
    /// Win32 resolution that truncates above 260 chars and discards trailing dot/space —
    /// the two vectors of T-01. On POSIX it is declared and documented identity: there is
    /// no limit nor rewriting. Already extended path returns untouched.
    /// </summary>
    public static string ToExtendedLength(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsWindows())
        {
            return path; // POSIX: no MAX_PATH limit; explicit and documented identity.
        }

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path; // idempotent
        }

        // UNC ("\\server\share\x") uses the extended namespace UNC device.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ExtendedUncPrefix + path[2..];
        }

        // Drive-relative path ("C:x.txt") needs the full path before the prefix.
        var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

        return ExtendedPrefix + full;
    }

    /// <summary>
    /// STRUCTURAL path combination with RE-CANONIZATION at each segment (R2:
    /// raw concatenation forbidden). Each segment is resolved against the accumulated
    /// base; a ROOTED (absolute) segment replaces the base on re-canonicalization — the
    /// deviation becomes EXPLICIT in the product instead of masked, and it is up to the
    /// caller to close it with <see cref="EnsureContained"/>, required at every
    /// write/move point in this product.
    /// Output is always in canonical extended form.
    /// </summary>
    public static string Combine(string root, params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var accumulated = Path.GetFullPath(root);

        foreach (var segment in segments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);

            // Structural re-canonicalization: rooted restarts resolution (visible
            // deviation), relative descends from base (GetFullPath resolves "."/"..",
            // duplicate separators and redundant components).
            accumulated = Path.IsPathRooted(segment)
                ? Path.GetFullPath(segment)
                : Path.GetFullPath(segment, accumulated);
        }

        return ToExtendedLength(accumulated);
    }

    /// <summary>
    /// BYTE-BYTE CONTAINMENT check (D4) of the resolved path against the canonical
    /// root. Call BEFORE every write/move operation. Ordinal comparison
    /// (UTF-16 code units): "/root-evil" vs "/root" is REJECTED by the boundary
    /// character check. Outside root ⇒ <see cref="PathEscapeException"/>.
    /// </summary>
    public static void EnsureContained(string path, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var resolved = ToExtendedLength(Path.GetFullPath(path));
        var canonicalRoot = CanonicalizeRoot(root);

        if (!resolved.StartsWith(canonicalRoot, StringComparison.Ordinal))
        {
            throw new PathEscapeException(path, root, resolved);
        }

        // Within the prefix: either it IS the root, or the next character MUST be a separator.
        if (resolved.Length > canonicalRoot.Length)
        {
            var boundary = resolved[canonicalRoot.Length];
            if (boundary != Path.DirectorySeparatorChar
                && boundary != Path.AltDirectorySeparatorChar
                && boundary != Path.VolumeSeparatorChar)
            {
                throw new PathEscapeException(path, root, resolved);
            }
        }
    }

    /// <summary>
    /// True if the NAME contains a bidi control character (fixed list above).
    /// PURE function over UTF-16 code units: no visual/locale fold — homoglyph is NOT
    /// detected here (homoglyph is another legitimate name; grouping stays on exact
    /// bytes at Level 1, SPEC §7). Feeds the structural marker
    /// <see cref="FileEntry.HasBidiControlChars"/> (D5) and report escaping (R12).
    /// </summary>
    public static bool HasBidiControlChars(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var table = BidiControls.Span;

        foreach (var c in name)
        {
            foreach (var forbidden in table)
            {
                if (c == forbidden)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// FAIL-CLOSED containment failure (ADR-0002 item 4; T-01/R2): a resolved path escaped
/// the canonical root prefix. Carries intended, root and resolved for audit.
/// No destructive operation proceeds after this exception (R11).
/// </summary>
public sealed class PathEscapeException : InvalidOperationException
{
    public PathEscapeException(string requestedPath, string root, string resolvedPath)
        : base($"Containment violated (T-01/R2): '{resolvedPath}' is outside canonical root '{root}' (requested: '{requestedPath}'). Operation refused.")
    {
        RequestedPath = requestedPath;
        Root = root;
        ResolvedPath = resolvedPath;
    }

    /// <summary>Path requested by the caller (before canonization).</summary>
    public string RequestedPath { get; }

    /// <summary>Canonical root required as byte-by-byte prefix.</summary>
    public string Root { get; }

    /// <summary>Path after re-canonicalization (extended form).</summary>
    public string ResolvedPath { get; }
}
