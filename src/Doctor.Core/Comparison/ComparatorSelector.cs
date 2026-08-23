namespace Doctor.Core;

/// <summary>
/// Seleção de comparador por extensão case-insensitive (ADR-0011 item 1;
/// contratos.md IDocumentComparator). .txt/.log/.ini/.cfg/.conf ⇒
/// <see cref="TextComparator"/>; .md ⇒ <see cref="MarkdownComparator"/>; .csv ⇒
/// <see cref="CsvComparator"/>; TODO o resto — inclusive arquivo SEM extensão — ⇒
/// <see cref="BinaryFallbackComparator"/> (desconhecido ⇒ binário). Formatos v1 do
/// ADR-0011 completos desde o EPIC 06.2 (card T24/t_f37457ba).
/// </summary>
public static class ComparatorSelector
{
    /// <summary>Ponto único de entrada da comparação de conteúdo (SPEC §16).</summary>
    public static ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        IDocumentComparator comparator = Selecionar(left.Path);
        return comparator.Compare(left, right, ct);
    }

    /// <summary>Roteia pela extensão do caminho, sem sensibilidade a caixa.</summary>
    public static IDocumentComparator Selecionar(string path)
    {
        var extensao = Path.GetExtension(path);
        if (MarkdownComparatorRota.Contains(extensao, StringComparer.OrdinalIgnoreCase))
        {
            return new MarkdownComparator();
        }

        if (CsvComparatorRota.Contains(extensao, StringComparer.OrdinalIgnoreCase))
        {
            return new CsvComparator();
        }

        bool texto = TextComparator.ExtensoesTexto.Contains(
            extensao, StringComparer.OrdinalIgnoreCase);
        return texto ? new TextComparator() : new BinaryFallbackComparator();
    }

    /// <summary>Extensões roteadas para MarkdownComparator (T24).</summary>
    private static readonly string[] MarkdownComparatorRota = { ".md" };

    /// <summary>Extensões roteadas para CsvComparator (T24).</summary>
    private static readonly string[] CsvComparatorRota = { ".csv" };
}
