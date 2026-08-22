namespace Doctor.Core;

/// <summary>
/// Registro de placeholder do relatório (schema-report-v1.md §6.3): caminho,
/// rótulos na ordem canônica declarada e tamanho obtido por metadados de
/// enumeração — nunca por abertura do arquivo.
/// </summary>
public sealed record PlaceholderRecord
{
    /// <summary>Caminho relativo, ordenação global por bytes.</summary>
    public required string Path { get; init; }

    /// <summary>Rótulos detectados, sem duplicatas, na ordem canônica:
    /// reparse_point → recall_on_data_access → recall_on_open → offline.
    /// Valores permitidos: somente esses quatro.</summary>
    public required IReadOnlyList<string> Kinds { get; init; }

    /// <summary>Tamanho visto no Level 0, sem abrir o arquivo.</summary>
    public required long SizeBytes { get; init; }
}

/// <summary>
/// T09 — projeção das entradas Level 0 para a lista Placeholders[] do relatório.
/// Os placeholders ficam fora de L1/L2/L3 por construção: o pipeline os consome
/// desta lista e nunca os entrega ao hasher (gate: <see cref="PlaceholderGuardedHasher"/>).
/// Telemetria: FilesPlaceholder conta as entradas; PlaceholderBytesRead permanece 0 —
/// invariante de segurança (SPEC §21).
/// </summary>
public static class PlaceholderReport
{
    /// <summary>Ordem canônica dos rótulos declarada no schema-report-v1.md §6.3.</summary>
    private static readonly (FileAttributes Bit, string Rotulo)[] OrdemCanonica =
    {
        (FileAttributes.ReparsePoint, "reparse_point"),
        (PlaceholderPolicy.RecallOnDataAccess, "recall_on_data_access"),
        (PlaceholderPolicy.RecallOnOpen, "recall_on_open"),
        (FileAttributes.Offline, "offline"),
    };

    /// <summary>
    /// Projeta TODA entrada marcada IsPlaceholder como <see cref="PlaceholderRecord"/>,
    /// ordenada por bytes de caminho (StringComparer.Ordinal sobre o caminho).
    /// </summary>
    public static IReadOnlyList<PlaceholderRecord> Records(IEnumerable<FileEntry> files) => files
        .Where(f => f.IsPlaceholder)
        .Select(f => new PlaceholderRecord { Path = f.Path, Kinds = Kinds(f), SizeBytes = f.Size })
        .OrderBy(r => r.Path, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Rótulos sem duplicatas, na ordem canônica declarada do schema.</summary>
    private static IReadOnlyList<string> Kinds(FileEntry entry)
    {
        var rotulos = new List<string>(4);
        foreach (var (bit, rotulo) in OrdemCanonica)
        {
            if ((entry.Attributes & bit) == bit)
            {
                rotulos.Add(rotulo);
            }
        }

        return rotulos;
    }
}
