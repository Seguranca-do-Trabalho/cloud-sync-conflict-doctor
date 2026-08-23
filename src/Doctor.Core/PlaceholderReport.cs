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

    /// <summary>
    /// Rótulos sem duplicatas, na ordem canônica declarada do schema. Fonte dupla,
    /// porque o motivo pode vir de dois lugares: dos bits crus (Windows nativo) ou
    /// da classificação já feita na origem (<see cref="FileEntry.PlaceholderKind"/> —
    /// inclui a simulação por sidecar da convenção T04).
    /// </summary>
    private static IReadOnlyList<string> Kinds(FileEntry entry)
    {
        var selecionados = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (bit, rotulo) in OrdemCanonica)
        {
            if ((entry.Attributes & bit) == bit)
            {
                selecionados.Add(rotulo);
            }
        }

        if (entry.PlaceholderKind is { } kind)
        {
            selecionados.Add(Rotulo(kind));
        }

        return OrdemCanonica
            .Where(linha => selecionados.Contains(linha.Rotulo))
            .Select(linha => linha.Rotulo)
            .ToArray();
    }

    private static string Rotulo(PlaceholderKind kind) => kind switch
    {
        PlaceholderKind.Offline => "offline",
        PlaceholderKind.RecallOnOpen => "recall_on_open",
        PlaceholderKind.RecallOnDataAccess => "recall_on_data_access",
        _ => "reparse_point",
    };
}
