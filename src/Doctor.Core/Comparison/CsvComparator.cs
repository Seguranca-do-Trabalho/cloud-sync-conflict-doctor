namespace Doctor.Core;

/// <summary>
/// CSV comparator (SPEC §16 CSV; ADR-0011 item 2; contracts.md
/// IDocumentComparator; orchestrator decisions in card T24/t_f37457ba). Subset
/// RFC4180 parsing: double-quoted fields, escaped quote ""; delimiter
/// detected by counting in the FIRST non-empty line of each file among
/// ',' ';' '\t'; DIFFERENT delimiters between files ⇒ structural difference:
/// ALL lines in <see cref="RegionKind.Changed"/> regions. No
/// header inference. No line reordering — order is meaning.
///
/// Alignment by <see cref="LcsDiff"/> over the NORMALIZED raw line; Changed pair
/// ⇒ cell-by-cell comparison: cells equal
/// post-parse ⇒ region reverts to <see cref="RegionKind.Equal"/> (redundant quotes
/// are not a semantic difference); different cell count or any divergent
/// cell ⇒ remains <see cref="RegionKind.Changed"/> for the entire line
/// (never sub-line region). Byte-by-byte determinism inherited from the engine; no
/// time field.
///
/// Inherited gate (ADR-0011 item 4): placeholder ⇒ <see cref="PlaceholderReadException"/>
/// BEFORE any opening — zero placeholder bytes read.
/// </summary>
public sealed class CsvComparator : IDocumentComparator
{
    private static readonly char[] CandidateDelimiters = { ',', ';', '\t' };

    /// <inheritdoc cref="IDocumentComparator.Compare"/>
    public ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        GatePlaceholder(left);
        GatePlaceholder(right);

        var leftLines = ReadNormalizedLines(left.Path);
        var rightLines = ReadNormalizedLines(right.Path);
        char delimLeft = DetectDelimiter(leftLines);
        char delimRight = DetectDelimiter(rightLines);

        List<DiffRegion> regions;
        if (delimLeft != delimRight)
        {
            // Structural difference: no line can match — entire documents
            // become a single Changed region.
            regions =
                leftLines.Count + rightLines.Count > 0
                    ? [new DiffRegion(RegionKind.Changed, 0, leftLines.Count, 0, rightLines.Count)]
                    : [];
        }
        else
        {
            var raw = LcsDiff.Diff(leftLines, rightLines);
            regions = RefineChangedPairs(raw, leftLines, rightLines, delimLeft);
        }

        bool equal = regions.Count == 0
            || regions.All(r => r.Kind == RegionKind.Equal);
        return new ComparisonResult("csv", equal, regions);
    }

    private static void GatePlaceholder(FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }
    }

    /// <summary>
    /// For each raw Changed pair, compares post-parse cells 1:1: if ALL
    /// paired lines have the same cells, the region reverts to Equal (quotes and
    /// delimiters are syntax); otherwise, stays Changed for the entire
    /// line. Added/Removed/Equal regions pass through untouched.
    /// </summary>
    private static List<DiffRegion> RefineChangedPairs(
        List<DiffRegion> regions,
        List<string> leftLines,
        List<string> rightLines,
        char delimiter)
    {
        var output = new List<DiffRegion>(regions.Count);
        foreach (var region in regions)
        {
            if (region.Kind != RegionKind.Changed)
            {
                output.Add(region);
                continue;
            }

            bool pairsEquivalent = true;
            int n = Math.Min(region.LeftCount, region.RightCount);
            for (int k = 0; k < n && pairsEquivalent; k++)
            {
                var cellsL = SplitIntoCells(leftLines[region.LeftStart + k], delimiter);
                var cellsR = SplitIntoCells(rightLines[region.RightStart + k], delimiter);
                if (cellsL.Count != cellsR.Count)
                {
                    pairsEquivalent = false;
                    break;
                }

                for (int c = 0; c < cellsL.Count; c++)
                {
                    if (!string.Equals(cellsL[c], cellsR[c], StringComparison.Ordinal))
                    {
                        pairsEquivalent = false;
                        break;
                    }
                }
            }

            output.Add(pairsEquivalent && n > 0
                ? new DiffRegion(RegionKind.Equal, region.LeftStart, region.LeftCount, region.RightStart, region.RightCount)
                : region);
        }

        return output;
    }

    /// <summary>
    /// Counts occurrences of each candidate in the first NON-EMPTY line and picks the
    /// most frequent; ties resolved by fixed order ',' ';' '\t'; none
    /// present or file with no non-empty line ⇒ ','. Empty line at the start does not
    /// drop the count to zero.
    /// </summary>
    private static char DetectDelimiter(List<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            char best = CandidateDelimiters[0];
            int bestCount = -1;
            foreach (var candidate in CandidateDelimiters)
            {
                int count = CountOccurrencesOutsideQuotes(line, candidate);
                if (count > bestCount)
                {
                    best = candidate;
                    bestCount = count;
                }
            }

            return best;
        }

        return ',';
    }

    /// <summary>Number of occurrences of <paramref name="c"/> outside quoted fields.</summary>
    private static int CountOccurrencesOutsideQuotes(string line, char c)
    {
        int count = 0;
        bool inside = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (inside && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i++; // "" is escaped quote inside quoted field
                }
                else
                {
                    inside = !inside;
                }
            }
            else if (ch == c && !inside)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Splits a raw line into cells (RFC4180 subset): outside quotes the
    /// delimiter separates fields; inside a quoted field the delimiter is literal
    /// and "" becomes a single quote. Outer quotes are removed.
    /// </summary>
    private static List<string> SplitIntoCells(string line, char delimiter)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inside = false;
        bool quotedField = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (!inside && current.Length == 0 && !quotedField)
                {
                    // opens quoted field at field start
                    inside = true;
                    quotedField = true;
                }
                else if (inside && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++; // "" → "
                }
                else if (inside)
                {
                    inside = false; // closes quoted field
                }
                else
                {
                    current.Append('"'); // literal quote outside quoted field
                }
            }
            else if (ch == delimiter && !inside)
            {
                cells.Add(current.ToString());
                current.Clear();
                quotedField = false;
            }
            else
            {
                current.Append(ch);
            }
        }

        cells.Add(current.ToString());
        return cells;
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
