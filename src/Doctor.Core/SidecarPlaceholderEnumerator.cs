namespace Doctor.Core;

using System.Text.Json;

/// <summary>
/// T04 convention (docs/test-strategy.md §5): on Linux/CI, Windows placeholders
/// are simulated by a sidecar <c>&lt;file&gt;.placeholder-meta.json</c> next to the
/// target file, because FILE_ATTRIBUTE_OFFLINE/RECALL_* only exist on NTFS/cfapi.
/// This decorator is the documented hook for this convention (and the exchange point
/// for the native enumerator on Windows):
/// 1. entry whose sidecar exists => marked IsPlaceholder with declared kind;
/// 2. the sidecar itself is tool METADATA, not user content: excluded from
///    Level 0 file list (never deleted nor read as data).
/// Telemetry always derived from the final list (same pattern as other enumerators).
/// </summary>
public sealed class SidecarPlaceholderEnumerator : IFileEnumerator
{
    /// <summary>Suffix reserved by the T04 convention.</summary>
    public const string SidecarSuffix = ".placeholder-meta.json";

    private readonly IFileEnumerator _inner;

    public SidecarPlaceholderEnumerator(IFileEnumerator inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
    {
        var result = _inner.Enumerate(rootPath, ct);

        var files = new List<FileEntry>(result.Files.Count);
        foreach (var entry in result.Files)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Path.EndsWith(SidecarSuffix, StringComparison.Ordinal))
            {
                continue; // convention metadata: outside user file list
            }

            var sidecar = entry.Path + SidecarSuffix;
            files.Add(File.Exists(sidecar)
                ? entry with { IsPlaceholder = true, PlaceholderKind = SidecarKind(sidecar) }
                : entry);
        }

        // Errors preserved; telemetry recalculated from post-convention final list.
        var telemetry = result.Telemetry with
        {
            FilesEnumerated = files.Count,
            FilesPlaceholder = files.Count(f => f.IsPlaceholder),
        };

        return new EnumerationResult(files, result.Errors, telemetry);
    }

    /// <summary>Kind declared in the sidecar (minimal T04 convention JSON); unknown
    /// or absent value falls back to conservative ReparsePoint ("do not touch"
    /// either way — the kind only labels the report).</summary>
    private static PlaceholderKind SidecarKind(string sidecarPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            var kind = document.RootElement.TryGetProperty("kind", out var property)
                ? property.GetString()
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
