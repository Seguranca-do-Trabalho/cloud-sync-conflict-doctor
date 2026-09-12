namespace Doctor.Core;

/// <summary>
/// Comparator selection by case-insensitive extension (ADR-0011 item 1;
/// contracts.md IDocumentComparator). .txt/.log/.ini/.cfg/.conf ⇒
/// <see cref="TextComparator"/>; .md ⇒ <see cref="MarkdownComparator"/>; .csv ⇒
/// <see cref="CsvComparator"/>; EVERYTHING else — including files WITHOUT extension — ⇒
/// <see cref="BinaryFallbackComparator"/> (unknown ⇒ binary). v1 formats from
/// ADR-0011 complete since EPIC 06.2 (card T24/t_f37457ba).
/// </summary>
public static class ComparatorSelector
{
    /// <summary>Single entry point for content comparison (SPEC §16).</summary>
    public static ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        IDocumentComparator comparator = Select(left.Path);
        return comparator.Compare(left, right, ct);
    }

    /// <summary>Routes by path extension, case-insensitive.</summary>
    public static IDocumentComparator Select(string path)
    {
        var extension = Path.GetExtension(path);
        if (MarkdownComparatorRouting.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new MarkdownComparator();
        }

        if (CsvComparatorRouting.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new CsvComparator();
        }

        bool isText = TextComparator.TextExtensions.Contains(
            extension, StringComparer.OrdinalIgnoreCase);
        return isText ? new TextComparator() : new BinaryFallbackComparator();
    }

    /// <summary>Extensions routed to MarkdownComparator (T24).</summary>
    private static readonly string[] MarkdownComparatorRouting = { ".md" };

    /// <summary>Extensions routed to CsvComparator (T24).</summary>
    private static readonly string[] CsvComparatorRouting = { ".csv" };
}
