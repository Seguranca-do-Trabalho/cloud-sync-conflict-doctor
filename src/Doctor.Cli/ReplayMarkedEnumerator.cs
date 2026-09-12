namespace Doctor.Cli;

using Doctor.Core;

/// <summary>
/// T16 — replay of the already-marked and ordered L0 list for the pipeline. ScanPipeline
/// reorders via OrderedFileEnumerator, whose projection queries PlaceholderPolicy;
/// with the hardened Classify (source marking is the ultimate authority), the
/// T04 sidecar convention mark survives any recomposition — including
/// this replay. No I/O; never alters Path, Size, MtimeUtc or marking.
/// </summary>
internal sealed class ReplayMarkedEnumerator : IFileEnumerator
{
    private readonly IReadOnlyList<FileEntry> _files;

    public ReplayMarkedEnumerator(IReadOnlyList<FileEntry> files) => _files = files;

    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) =>
        new(_files, Array.Empty<ScanError>(), new ScanTelemetry());
}
