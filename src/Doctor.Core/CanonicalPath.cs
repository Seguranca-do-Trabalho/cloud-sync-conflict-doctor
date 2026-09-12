namespace Doctor.Core;

/// <summary>
/// Canon for relative paths and defense against hostile names (card T17 — sprint S11;
/// SPEC §35 Security agent "path traversal"; GATE 5 "path/reparse attacks tested";
/// ADR-0002 fail-closed philosophy).
///
/// Contract of <see cref="IsValidName"/>: approves ONLY safe relative names —
/// rejects null/empty, control characters (&lt; 0x20 and DEL), empty segment
/// ("//" sequences, leading/trailing separator in any variant), any
/// segment "." or ".." (traversal is never silently sanitized), NTFS reserved
/// names (CON, PRN, AUX, NUL, COM1-9, LPT1-9 — recognized by Windows as
/// STEM before the first dot, in any case, in any path segment), trailing dot
/// or space (Win32 discards these suffixes on creation — confusion vector) and
/// extension above 255 characters (component that NTFS would not store cannot
/// pass the gate).
///
/// Contract of <see cref="Normalize"/>: canonizes separator '\' → '/', preserves
/// original case (canonical comparison is left to
/// <see cref="StringComparer.OrdinalIgnoreCase"/> in the consumer — NTFS equality
/// semantics; do NOT confuse with <see cref="PathOrder"/>, which sorts the
/// report by Ordinal case-sensitive per SPEC §3) and truncates extension
/// above 255 to 255 — the only repair allowed. Any other hostile name
/// throws <see cref="ArgumentException"/>: fail-closed, never sanitized output.
///
/// The canonical path is RELATIVE to the scan root: absolute path ("/x", "C:\x")
/// produces empty segment/drive and is rejected — the tree boundary is the
/// responsibility of the enumeration (ADR-0004), and this type ensures nothing
/// approved escapes it.
/// </summary>
public static class CanonicalPath
{
    /// <summary>Win32/NTFS reserved table: exact name (before the first dot) forbidden in any segment.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Extension ceiling (card T17): above this the component is hostile until repaired.</summary>
    public const int MaxExtensionLength = 255;

    /// <summary>
    /// Name gate: true only if <paramref name="path"/> is a safe relative path
    /// per the type's documentation criteria. Fail-closed — when in doubt,
    /// false.
    /// </summary>
    public static bool IsValidName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c < ' ' || c == '\u007F')
            {
                return false; // control character (includes DEL)
            }
        }

        var segments = path.Split('/', '\\');

        foreach (var segment in segments)
        {
            // Empty segment: "//" sequence, separator at edges or absolute path.
            // "." and "..": traversal at any position — never approved.
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }

            // Win32 discards trailing dot/space when creating the component; approving
            // would endorse a name the volume rewrites.
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return false;
            }

            // NTFS reservation applies to the STEM (portion before the first dot),
            // in any case — "con.txt" collides with the CON device as much as "CON".
            var dot = segment.IndexOf('.');
            var stem = dot < 0 ? segment : segment[..dot];

            if (ReservedNames.Contains(stem))
            {
                return false;
            }
        }

        // Extension of the LAST segment: component with more than 255 characters of
        // extension does not exist in NTFS; the gate does not approve the impossible
        // (the legitimate route is repair by <see cref="Normalize"/>).
        var last = segments[^1];
        var lastDot = last.LastIndexOf('.');

        if (lastDot >= 0 && last.Length - lastDot - 1 > MaxExtensionLength)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Canonical form of the path: separator '/', original case preserved and
    /// extension truncated to 255 when it exceeds the ceiling. Rejects with
    /// <see cref="ArgumentException"/> every hostile name except the long extension
    /// case — never returns sanitized form of traversal or reserved.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Null or empty path has no canonical form.", nameof(path));
        }

        var canonical = path.Replace('\\', '/');

        if (IsValidName(canonical))
        {
            return canonical;
        }

        // Only allowed repair: extension above 255 is truncated to ceiling.
        var repaired = TruncateExtension(canonical);

        if (!ReferenceEquals(repaired, canonical) && IsValidName(repaired))
        {
            return repaired;
        }

        throw new ArgumentException(
            $"Hostile path rejected by canon (traversal, NTFS reserved, control or invalid structure): \"{path}\".",
            nameof(path));
    }

    /// <summary>
    /// Returns the path with the last segment's extension truncated to 255
    /// characters, or the SAME instance when there is no repair to make (avoids
    /// allocation in the common case and lets the caller distinguish the cases).
    /// </summary>
    private static string TruncateExtension(string path)
    {
        var lastSeparator = path.LastIndexOf('/');
        var lastDot = path.LastIndexOf('.');
        var extensionStart = lastDot + 1;

        // Dot must be in the last segment and open an extension that's too long.
        if (lastDot < 0 || lastDot < lastSeparator || extensionStart <= lastSeparator)
        {
            return path;
        }

        var extensionLength = path.Length - extensionStart;

        if (extensionLength <= MaxExtensionLength)
        {
            return path;
        }

        return path.Remove(extensionStart, extensionLength - MaxExtensionLength);
    }
}
