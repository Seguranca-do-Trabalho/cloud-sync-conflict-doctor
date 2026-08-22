namespace Doctor.Core;

using System.Text.Json;

/// <summary>
/// Convenção T04 (docs/test-strategy.md §5): em Linux/CI os placeholders do Windows
/// são simulados por um sidecar <c>&lt;arquivo&gt;.placeholder-meta.json</c> ao lado do
/// arquivo alvo, porque FILE_ATTRIBUTE_OFFLINE/RECALL_* só existem em NTFS/cfapi.
/// Este decorador é o hook documentado dessa convenção (e o ponto de troca pelo
/// enumerador nativo no Windows):
/// 1. entrada cujo sidecar existe => marcada IsPlaceholder com o kind declarado;
/// 2. o próprio sidecar é METADADO da ferramenta, não conteúdo do usuário: sai da
///    lista Level 0 (nunca é apagado nem lido como dado).
/// Telemetria sempre derivada da lista final (mesmo padrão dos demais enumeradores).
/// </summary>
public sealed class SidecarPlaceholderEnumerator : IFileEnumerator
{
    /// <summary>Sufixo reservado pela convenção T04.</summary>
    public const string SufixoSidecar = ".placeholder-meta.json";

    private readonly IFileEnumerator _inner;

    public SidecarPlaceholderEnumerator(IFileEnumerator inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
    {
        var resultado = _inner.Enumerate(rootPath, ct);

        var arquivos = new List<FileEntry>(resultado.Files.Count);
        foreach (var entry in resultado.Files)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Path.EndsWith(SufixoSidecar, StringComparison.Ordinal))
            {
                continue; // metadado da convenção: fora da lista de arquivos do usuário
            }

            var sidecar = entry.Path + SufixoSidecar;
            arquivos.Add(File.Exists(sidecar)
                ? entry with { IsPlaceholder = true, PlaceholderKind = KindDoSidecar(sidecar) }
                : entry);
        }

        // Erros preservados; telemetria recalculada da lista final pós-convenção.
        var telemetry = resultado.Telemetry with
        {
            FilesEnumerated = arquivos.Count,
            FilesPlaceholder = arquivos.Count(f => f.IsPlaceholder),
        };

        return new EnumerationResult(arquivos, resultado.Errors, telemetry);
    }

    /// <summary>Kind declarado no sidecar (JSON mínimo da convenção T04); valor
    /// desconhecido ou ausente cai no conservador ReparsePoint ("não tocar" de
    /// qualquer forma — o kind só rotula o relatório).</summary>
    private static PlaceholderKind KindDoSidecar(string caminhoSidecar)
    {
        try
        {
            using var documento = JsonDocument.Parse(File.ReadAllText(caminhoSidecar));
            var kind = documento.RootElement.TryGetProperty("kind", out var propriedade)
                ? propriedade.GetString()
                : null;
            return kind switch
            {
                "offline" => PlaceholderKind.Offline,
                "recall_on_open" => PlaceholderKind.RecallOnOpen,
                "recall_on_data_access" => PlaceholderKind.RecallOnDataAccess,
                _ => PlaceholderKind.ReparsePoint,
            };
        }
        catch (JsonException)
        {
            return PlaceholderKind.ReparsePoint;
        }
    }
}
