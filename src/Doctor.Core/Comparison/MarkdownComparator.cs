namespace Doctor.Core;

/// <summary>
/// Markdown comparator (SPEC §16 Markdown; ADR-0011 item 2; contracts.md
/// IDocumentComparator; orchestrator decisions in card T24/t_f37457ba). Base:
/// line-by-line text diff over <see cref="LcsDiff"/>. LIGHT structural layer
/// on top — the ONLY recognized features are: ATX heading lines
/// (#{1..6} followed by space/end of line, in column 0) OUTSIDE ``` fences and
/// fence balance. No AST, no markdown parser, no external dependency.
///
/// Refinement rule: non-<see cref="RegionKind.Equal"/> region whose span
/// (left or right) contains a heading and whose heading SET differs between
/// left/right ⇒ <see cref="RegionKind.Changed"/> preserving the original spans
/// (even if raw LCS said Added/Removed). Common-text-only difference
/// remains Added/Removed. Headings inside a fence do not count; an open
/// fence without closing swallows the rest of the document (no heading after
/// it counts). Byte-by-byte determinism inherited from the engine; no time field.
///
/// Inherited gate (ADR-0011 item 4): placeholder ⇒ <see cref="PlaceholderReadException"/>
/// BEFORE any opening — zero placeholder bytes read. The normalized line
/// reader is self-contained in this class (same semantics as
/// TextComparator, whose private members are not touched by this card).
/// </summary>
public sealed class MarkdownComparator : IDocumentComparator
{
    /// <inheritdoc cref="IDocumentComparator.Compare"/>
    public ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        GatePlaceholder(left);
        GatePlaceholder(right);

        var leftLines = ReadNormalizedLines(left.Path);
        var rightLines = ReadNormalizedLines(right.Path);

        var regions = LcsDiff.Diff(leftLines, rightLines);
        var refined = RefineByHeadings(regions, leftLines, rightLines);

        bool equal = refined.Count == 0
            || refined.All(r => r.Kind == RegionKind.Equal);
        return new ComparisonResult("markdown", equal, refined);
    }

    private static void GatePlaceholder(FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }
    }

    /// <summary>
    /// Applies the refinement rule: replaces with <see cref="RegionKind.Changed"/>
    /// each non-Equal region whose span contains a heading on either side and whose
    /// heading sets diverge between sides. Remaining regions pass through untouched.
    /// Heading candidates are precomputed by a GLOBAL document scan (fence state
    /// crosses region boundaries — a span starting in the middle of an open fence
    /// inherits that state).
    /// </summary>
    private static List<DiffRegion> RefineByHeadings(
        List<DiffRegion> regions,
        List<string> leftLines,
        List<string> rightLines)
    {
        var leftIndices = HeadingIndices(leftLines);
        var rightIndices = HeadingIndices(rightLines);

        var output = new List<DiffRegion>(regions.Count);
        foreach (var region in regions)
        {
            if (region.Kind == RegionKind.Equal)
            {
                output.Add(region);
                continue;
            }

            var headLeft = HeadingsInSpan(leftLines, leftIndices, region.LeftStart, region.LeftCount);
            var headRight = HeadingsInSpan(rightLines, rightIndices, region.RightStart, region.RightCount);

            bool spanHasHeading = headLeft.Count > 0 || headRight.Count > 0;
            if (spanHasHeading && !headLeft.SetEquals(headRight))
            {
                output.Add(new DiffRegion(
                    RegionKind.Changed,
                    region.LeftStart, region.LeftCount,
                    region.RightStart, region.RightCount));
                continue;
            }

            output.Add(region);
        }

        return output;
    }

    /// <summary>
    /// (Global) indices of document heading lines: single scan where
    /// ``` fences toggle state — an OPEN fence never closed disables
    /// recognition until end of document.
    /// </summary>
    private static HashSet<int> HeadingIndices(List<string> lines)
    {
        var indices = new HashSet<int>();
        bool inFence = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (IsFenceLine(line))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && IsAtxHeading(line))
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    /// <summary>
    /// (Ordinal) set of heading lines in the range [start, start+count),
    /// restricted to indices precomputed as headings outside fences.
    /// </summary>
    private static HashSet<string> HeadingsInSpan(
        List<string> lines, HashSet<int> headingIndices, int start, int count)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        int end = Math.Min(start + count, lines.Count);
        for (int i = start; i < end; i++)
        {
            if (headingIndices.Contains(i))
            {
                set.Add(lines[i]);
            }
        }

        return set;
    }

    /// <summary>Fence line: starts (after whitespace) with three backticks.</summary>
    private static bool IsFenceLine(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    /// <summary>Strict ATX heading: 1 to 6 hash marks in column 0, then space, tab or end of line.</summary>
    private static bool IsAtxHeading(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == '#')
        {
            i++;
        }

        return i is >= 1 and <= 6
            && (i == line.Length || line[i] == ' ' || line[i] == '\t');
    }

    /// <summary>Reads the file as UTF-8, removes BOM if present and splits into LF-ended lines (no trim).</summary>
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
