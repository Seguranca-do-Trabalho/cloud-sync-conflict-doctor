namespace Doctor.Core;

/// <summary>
/// Classificação do motivo pelo qual uma entrada é placeholder (SPEC §6).
/// Ordem de precedência na classificação: OFFLINE → RECALL_ON_OPEN →
/// RECALL_ON_DATA_ACCESS → ReparsePoint.
/// </summary>
public enum PlaceholderKind
{
    Offline,
    RecallOnOpen,
    RecallOnDataAccess,
    ReparsePoint,
}

/// <summary>
/// Regra crítica de segurança (SPEC §6): entrada marcada como placeholder é "NÃO TOCAR" —
/// nunca abrir, nunca hashear, nunca obter conteúdo. Policy PURA sobre os metadados já
/// coletados no Level 0: não faz I/O, não abre arquivo, não depende de plataforma.
/// </summary>
public static class PlaceholderPolicy
{
    // Bits FILE_ATTRIBUTE_* que não existem em System.IO.FileAttributes no .NET 8:
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;       // FILE_ATTRIBUTE_RECALL_ON_OPEN
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    /// <summary>Verdadeiro se a entrada NUNCA pode ter conteúdo lido.</summary>
    public static bool IsPlaceholder(FileEntry entry) => Classify(entry) is not null;

    /// <summary>
    /// Sobrecarga sobre atributos crus (sem <see cref="FileEntry"/>): usada pelos
    /// pontos que decidem ANTES de construir a entrada (ex.: recusa de descida em
    /// diretório com reparse — ADR-0004 regra 4). Mantém os bits "não tocar"
    /// centralizados aqui, único ponto de decisão do produto.
    /// </summary>
    public static bool IsPlaceholder(FileAttributes attributes) =>
        (attributes & FileAttributes.Offline) != 0
        || (attributes & RecallOnOpen) != 0
        || (attributes & RecallOnDataAccess) != 0
        || (attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Retorna o kind do placeholder ou null se a entrada for segura para leitura.
    /// Um único bit suficiente: qualquer um dos quatro dispara "NÃO TOCAR".
    /// Regra T16 (falha fechada, SPEC §6): marcação já feita na ORIGEM
    /// (<see cref="FileEntry.PlaceholderKind"/> — convenção T04 de sidecar hoje,
    /// hook nativo amanhã) é AUTORIDADE MÁXIMA e nunca é apagada por projeção
    /// posterior de enumerador: quem classifica primeiro decide; bits crus só
    /// ACRESCENTAM suspeita, nunca removem marca. Em árvore nativa do Windows o
    /// comportamento é idêntico ao anterior (kind nulo ⇒ decisão pelos bits).
    /// </summary>
    public static PlaceholderKind? Classify(FileEntry entry)
    {
        if (entry.PlaceholderKind is { } daOrigem)
        {
            return daOrigem;
        }

        var a = entry.Attributes;

        if ((a & FileAttributes.Offline) != 0)                 return PlaceholderKind.Offline;            // 0x1000
        if ((a & RecallOnOpen) != 0)                           return PlaceholderKind.RecallOnOpen;       // 0x40000
        if ((a & RecallOnDataAccess) != 0)                     return PlaceholderKind.RecallOnDataAccess; // 0x400000
        if ((a & FileAttributes.ReparsePoint) != 0)            return PlaceholderKind.ReparsePoint;       // 0x400

        return null;
    }
}
