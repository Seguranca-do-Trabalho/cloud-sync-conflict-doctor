namespace Doctor.Core;

/// <summary>
/// Ordem canônica do produto (SPEC §3, ADR-0003, docs/contratos.md): caminho comparado
/// byte a byte em UTF-8 — StringComparer.Ordinal — e NUNCA OrdinalIgnoreCase.
///
/// Escolha documentada: StringComparer.Ordinal compara ponto de código Unicode a ponto
/// de código; como .NET strings são UTF-16 e todos os caracteres BMP relevantes de
/// caminhos se codificam em UTF-8 na mesma ordem relativa dos pontos de código
/// (ordem de code point preserva a ordem dos bytes UTF-8 para escalares válidos),
/// Ordinal é a materialização estável e independente de locale da "ordem por bytes
/// UTF-8". Qualquer comparador cultural ou case-insensitive variaria entre máquinas
/// e versões de ICU/NLS, violando o determinismo byte-a-byte do §3.
/// Empate de caminho é impossível dentro de uma árvore (caminhos são únicos);
/// o desempate mtime → size → path do ADR-0003 aplica-se a decisões entre ENTRADAS
/// distintas (grupos, conflitos) e vive no pipeline, não nesta comparação de caminhos.
/// </summary>
public static class PathOrder
{
    /// <summary>Comparador canônico de FileEntry por caminho byte-a-byte.</summary>
    public static readonly IComparer<FileEntry> Comparer =
        Comparer<FileEntry>.Create((a, b) =>
            string.CompareOrdinal(a.Path, b.Path));

    /// <summary>Ordena qualquer coleção pela chave canônica de um FileEntry associado.</summary>
    public static IOrderedEnumerable<T> Sort<T>(
        IEnumerable<T> items,
        Func<T, FileEntry> key) => items.OrderBy(key, Comparer);
}
