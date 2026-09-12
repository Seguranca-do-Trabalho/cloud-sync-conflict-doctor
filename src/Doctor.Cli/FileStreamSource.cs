namespace Doctor.Cli;

using Doctor.Core;

/// <summary>
/// T16 — single content source for the CLI layer (contracts.md IStreamSource): every
/// L3 open goes exclusively through here, after the placeholder gate.
/// Read-only; no writes, no deletions (ADR-0002).
/// </summary>
public sealed class FileStreamSource : IStreamSource
{
    public Stream OpenRead(FileEntry entry) => File.OpenRead(entry.Path);
}
