namespace Doctor.Core;

/// <summary>
/// Seleção de comparador por extensão case-insensitive (ADR-0011 item 1;
/// contratos.md IDocumentComparator). .txt/.log/.ini/.cfg/.conf ⇒
/// <see cref="TextComparator"/>; TODO o resto — inclusive arquivo SEM extensão — ⇒
/// <see cref="BinaryFallbackComparator"/> (desconhecido ⇒ binário). Markdown/CSV
/// ganham comparadores próprios em cards futuros (EPIC 06.2).
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
        bool texto = TextComparator.ExtensoesTexto.Contains(
            extensao, StringComparer.OrdinalIgnoreCase);
        return texto ? new TextComparator() : new BinaryFallbackComparator();
    }
}
