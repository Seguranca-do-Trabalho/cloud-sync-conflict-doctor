namespace Doctor.Core;

/// <summary>
/// Metadados coletados no Level 0 (SPEC §5). Imutável após criação.
/// Contrato: docs/contratos.md. VolumeId/FileId alimentam a chave lógica do cache (SPEC §12).
/// </summary>
public sealed record FileEntry
{
    /// <summary>Caminho completo normalizado.</summary>
    public required string Path { get; init; }

    public required long Size { get; init; }

    public required DateTimeOffset MtimeUtc { get; init; }

    public required System.IO.FileAttributes Attributes { get; init; }

    /// <summary>Identificador estável do volume (fonte do par (volume_id, file_id) do cache).</summary>
    public required string VolumeId { get; init; }

    /// <summary>NTFS file ID / inode equivalente — nunca o caminho (SPEC §12).</summary>
    public required string FileId { get; init; }

    /// <summary>Verdadeiro se NUNCA se pode abrir o conteúdo (SPEC §6). Marcado pelo enumerador ordenado.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>Motivo da marcação de placeholder; null quando não é placeholder.</summary>
    public PlaceholderKind? PlaceholderKind { get; init; }
}
