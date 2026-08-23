namespace Doctor.Cli;

using Doctor.Core;

/// <summary>
/// T16 — fonte única de conteúdo da camada CLI (contratos.md IStreamSource): toda
/// abertura do L3 passa exclusivamente por aqui, já após o gate de placeholder.
/// Somente-leitura; nenhuma escrita, nenhuma deleção (ADR-0002).
/// </summary>
public sealed class FileStreamSource : IStreamSource
{
    public Stream OpenRead(FileEntry entry) =>
        File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
}
