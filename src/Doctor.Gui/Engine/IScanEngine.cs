namespace Doctor.Gui.Engine;

/// <summary>
/// Contrato único entre a GUI e qualquer motor de scan (falso ou real).
/// A GUI não conhece implementação: consome somente esta interface.
/// O motor real (Doctor.Core) substitui FakeScanEngine sem tocar nas telas.
/// </summary>
public interface IScanEngine
{
    /// <summary>Executa o scan da pasta informada, reportando progresso 0..100.</summary>
    ScanReport Scan(string rootPath, Action<int>? progress = null);
}

/// <summary>
/// Modelo de domínio mínimo consumido pela GUI — espelha o schema do relatório
/// v1 de docs/adr/ADR-0003.md. Quando Doctor.Core existir, estes tipos passam a
/// ser reexportados/adaptados do Core; os nomes dos campos seguem o schema.
/// </summary>
public sealed class ScanReport
{
    public int ReportSchemaVersion { get; init; } = 1;
    public string Algorithm { get; init; } = "BLAKE3";
    public int HashVersion { get; init; } = 1;
    public string RootPath { get; init; } = "";
    public ScanTelemetry Telemetry { get; init; } = new();
    public IReadOnlyList<DuplicateGroup> IdenticalDuplicates { get; init; } = [];
    public IReadOnlyList<ConflictGroup> RealConflicts { get; init; } = [];
    public IReadOnlyList<PlaceholderFile> Placeholders { get; init; } = [];

    /// <summary>Duplicatas idênticas = cópias redundantes removíveis (não conta a cópia mantida).</summary>
    public int IdenticalDuplicateCount => IdenticalDuplicates.Sum(g => g.Files.Count - 1);

    /// <summary>Espaço recuperável com segurança: soma dos bytes das cópias redundantes.</summary>
    public long RecoverableBytes => IdenticalDuplicates.Sum(g => (long)(g.Files.Count - 1) * g.SizeBytes);
}

public sealed class ScanTelemetry
{
    public int FilesEnumerated { get; init; }
    public int FilesSkipped { get; init; }
    public int FilesPlaceholder { get; init; }
    public long BytesRead { get; init; }
    /// <summary>Regra absoluta §7: placeholder jamais é aberto.</summary>
    public long PlaceholderBytesRead { get; init; }
}

public sealed class DuplicateGroup
{
    public string Blake3Hash { get; init; } = "";
    public long SizeBytes { get; init; }
    /// <summary>Ordenado por caminho em bytes UTF-8 (regra de determinismo ADR-0003).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

public sealed class ConflictGroup
{
    /// <summary>Caminho base do grupo (sem sufixo de conflito).</summary>
    public string BaseName { get; init; } = "";
    public IReadOnlyList<ConflictVersion> Versions { get; init; } = [];
}

public sealed class ConflictVersion
{
    public string Path { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTimeOffset MtimeUtc { get; init; }
    public string Blake3Hash { get; init; } = "";
    public bool IsPlaceholder { get; init; }
}

public sealed class PlaceholderFile
{
    public string Path { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Reason { get; init; } = "";
}
