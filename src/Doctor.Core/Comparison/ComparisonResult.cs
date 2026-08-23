namespace Doctor.Core;

/// <summary>
/// Contrato de comparação de documentos (docs/contratos.md — fonte única; SPEC §16;
/// ADR-0011). Seleção por extensão case-insensitive; tipo desconhecido ⇒
/// <see cref="BinaryFallbackComparator"/>. Ambos os arquivos devem ser legíveis e
/// não-placeholder (gate herdado do hasher, ADR-0011 item 4) — placeholder lança
/// <see cref="PlaceholderReadException"/> ANTES de qualquer abertura.
/// Resultado estruturado, ordenado, sem campo incidental de tempo.
/// </summary>
public interface IDocumentComparator
{
    /// <summary>Compara o conteúdo dos dois arquivos e devolve regiões ordenadas por posição.</summary>
    ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct);
}

/// <summary>
/// Resultado estruturado da comparação (contratos.md — exato). <see cref="Regions"/>
/// em ordem crescente de posição no documento; nenhuma informação de relógio.
/// </summary>
public sealed record ComparisonResult(
    string ComparatorKind,
    bool AreSemanticallyEqual,
    IReadOnlyList<DiffRegion> Regions);
