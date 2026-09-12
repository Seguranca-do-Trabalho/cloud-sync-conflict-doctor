namespace Doctor.Core;

/// <summary>
/// Line-by-line text comparator (SPEC §16 "Text"; ADR-0011 item 2; contracts.md
/// IDocumentComparator). Normalization before diff: CRLF→LF; BOM (UTF-8/UTF-16)
/// recognized and ignored — BOM divergence alone does not make files different.
/// Post-normalization exact line comparison, no trim. Engine:
/// <see cref="LcsDiff"/> (ADR-0011 item 5 — own LCS, no external dependency).
/// Inherited gate (ADR-0011 item 4): placeholder ⇒ <see cref="PlaceholderReadException"/>
/// BEFORE any opening — zero placeholder bytes read.
/// </summary>
public sealed class TextComparator : IDocumentComparator
{
    /// <summary>Extensions treated as plain text in v1 (orchestrator decision, card T23).</summary>
    public static readonly IReadOnlyList<string> TextExtensions = new[]
    {
        ".txt", ".log", ".ini", ".cfg", ".conf",
    };

    /// <inheritdoc cref="IDocumentComparator.Compare"/>
    public ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        GatePlaceholder(left);
        GatePlaceholder(right);

        var leftLines = ReadNormalizedLines(left.Path);
        var rightLines = ReadNormalizedLines(right.Path);

        var regions = LcsDiff.Diff(leftLines, rightLines);
        bool equal = regions.Count == 0
            || regions.All(r => r.Kind == RegionKind.Equal);
        return new ComparisonResult("text", equal, regions);
    }

    private static void GatePlaceholder(FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }
    }

    /// <summary>Reads the file as UTF-8, removes BOM if present and splits into LF-ended lines.</summary>
    private static List<string> ReadNormalizedLines(string path)
    {
        using var reader = new StreamReader(path);
        var text = reader.ReadToEnd();
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (text.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..i]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }
}
