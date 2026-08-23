namespace Doctor.Core;

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 合同 do gravador de relatorio v1 (schema-report-v1.md).
/// Recebe o resultado serial do pipeline + telemetria + placeholders + metadados
/// de tempo/caminho e produz JSON estrito RFC 8259, UTF-8 sem BOM, indent 2,
/// chaves na ordem EXATA declarada do schema, newline final.
/// </summary>
public interface IReportWriter
{
    void Write(
        ScanResult result,
        ScanTelemetry telemetry,
        IReadOnlyList<PlaceholderRecord> placeholders,
        string rootPath,
        DateTimeOffset started,
        DateTimeOffset finished,
        Stream output);
}

/// <summary>
/// Gravador JSON v1 (schema-report-v1.md §2). Usa System.Text.Json com
/// WriteIndented=true, JavaScriptEncoder.UnsafeRelaxedJsonEscaping e POCO
/// cuja ordem de propriedades replica o schema. Anexa \n final; codificacao
/// UTF-8 sem BOM.
/// </summary>
public sealed class ReportWriterJson : IReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Write(
        ScanResult result,
        ScanTelemetry telemetry,
        IReadOnlyList<PlaceholderRecord> placeholders,
        string rootPath,
        DateTimeOffset started,
        DateTimeOffset finished,
        Stream output)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(placeholders);
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(output);

        rootPath = rootPath.Replace('\\', '/');
        var normRoot = rootPath.EndsWith("/") ? rootPath : rootPath + "/";

        var report = new ReportDoc
        {
            // SEG-12: campo novo em telemetry (files_excluded_conflictdoctor) ⇒ bump
            // obrigatório por §7.1 do schema-report-v1 (política conservadora: bump em
            // mudança aditiva; consumidores são estritos e consomem exatamente uma versão).
            ReportSchemaVersion = 2,
            Algorithm = "BLAKE3",
            HashVersion = 1,
            NormalizationRulesVersion = Grouping.NormalizerVersion,
            GeneratedFrom = new GenFromDoc
            {
                RootPath = rootPath,
                ScanStartedUtc = FormatTs(started),
                ScanFinishedUtc = FormatTs(finished),
            },
            Telemetry = new TelDoc
            {
                FilesEnumerated = telemetry.FilesEnumerated,
                FilesPlaceholder = telemetry.FilesPlaceholder,
                // SEG-12 (T-15): subárvore reservada excluída na fronteira canônica L0,
                // contada aqui — política, não erro (R10); idempotência §20 entre rescans.
                FilesExcludedConflictDoctor = telemetry.FilesExcludedConflictDoctor,
                FilesSkipped = telemetry.FilesSkipped,
                FilesPartialHashed = telemetry.FilesPartialHashed,
                FilesFullHashed = telemetry.FilesFullHashed,
                BytesRead = telemetry.BytesRead,
                BytesReadPartial = telemetry.BytesReadPartial,
                BytesReadFull = telemetry.BytesReadFull,
                PlaceholderBytesRead = telemetry.PlaceholderBytesRead,
            },
            Groups = result.Groups.Select(g => MapGroup(g, normRoot)).ToArray(),
            IdenticalDuplicates = result.IdenticalDuplicates.Select(d => MapDup(d, normRoot)).ToArray(),
            RealConflicts = result.RealConflicts.Select(c => MapConflict(c, normRoot)).ToArray(),
            Placeholders = placeholders.Select(MapPh).ToArray(),
        };

        var json = JsonSerializer.Serialize(report, Options);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        output.Write(bytes);
    }

    private static GroupDoc MapGroup(ConflictGroup g, string root) => new()
    {
        NormalizedBaseName = g.NormalizedBaseName,
        SizeBytes = g.SizeBytes,
        Members = g.Members.Select(m => new MemberDoc { Path = RelPath(root, m.Path) }).ToArray(),
    };

    private static DupDoc MapDup(IdenticalDuplicate d, string root) => new()
    {
        Hash = d.Hash,
        SizeBytes = d.SizeBytes,
        Files = d.Files.Select(f => RelPath(root, f.Path)).ToArray(),
    };

    private static ConflictDoc MapConflict(RealConflict c, string root) => new()
    {
        NormalizedBaseName = c.NormalizedBaseName,
        SizeBytes = c.SizeBytes,
        Files = c.Files.Select(f => new CFileDoc { Path = RelPath(root, f.Path), Hash = f.Hash }).ToArray(),
    };

    private static PhDoc MapPh(PlaceholderRecord p) => new()
    {
        Path = p.Path,
        Kinds = p.Kinds.ToArray(),
        SizeBytes = p.SizeBytes,
    };

    private static string RelPath(string root, string fullPath)
    {
        var p = fullPath.Replace('\\', '/');
        return p.StartsWith(root, StringComparison.Ordinal) ? p[root.Length..] : p;
    }

    private static string FormatTs(DateTimeOffset dt) =>
        dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // ---- DTOs: property order = schema order, JsonPropertyName = snake_case ----

    private sealed class ReportDoc
    {
        [JsonPropertyName("report_schema_version")] public int ReportSchemaVersion { get; init; }
        [JsonPropertyName("algorithm")] public string Algorithm { get; init; } = "";
        [JsonPropertyName("hash_version")] public int HashVersion { get; init; }
        [JsonPropertyName("normalization_rules_version")] public int NormalizationRulesVersion { get; init; }
        [JsonPropertyName("generated_from")] public GenFromDoc GeneratedFrom { get; init; } = new();
        [JsonPropertyName("telemetry")] public TelDoc Telemetry { get; init; } = new();
        [JsonPropertyName("groups")] public GroupDoc[] Groups { get; init; } = [];
        [JsonPropertyName("identical_duplicates")] public DupDoc[] IdenticalDuplicates { get; init; } = [];
        [JsonPropertyName("real_conflicts")] public ConflictDoc[] RealConflicts { get; init; } = [];
        [JsonPropertyName("placeholders")] public PhDoc[] Placeholders { get; init; } = [];
    }

    private sealed class GenFromDoc
    {
        [JsonPropertyName("root_path")] public string RootPath { get; init; } = "";
        [JsonPropertyName("scan_started_utc")] public string ScanStartedUtc { get; init; } = "";
        [JsonPropertyName("scan_finished_utc")] public string ScanFinishedUtc { get; init; } = "";
    }

    private sealed class TelDoc
    {
        [JsonPropertyName("files_enumerated")] public long FilesEnumerated { get; init; }
        [JsonPropertyName("files_placeholder")] public long FilesPlaceholder { get; init; }
        // SEG-12 (T-15): posição declarada no schema v2 §5 — logo após files_placeholder.
        [JsonPropertyName("files_excluded_conflictdoctor")] public long FilesExcludedConflictDoctor { get; init; }
        [JsonPropertyName("files_skipped")] public long FilesSkipped { get; init; }
        [JsonPropertyName("files_partial_hashed")] public long FilesPartialHashed { get; init; }
        [JsonPropertyName("files_full_hashed")] public long FilesFullHashed { get; init; }
        [JsonPropertyName("bytes_read")] public long BytesRead { get; init; }
        [JsonPropertyName("bytes_read_partial")] public long BytesReadPartial { get; init; }
        [JsonPropertyName("bytes_read_full")] public long BytesReadFull { get; init; }
        [JsonPropertyName("placeholder_bytes_read")] public long PlaceholderBytesRead { get; init; }
    }

    private sealed class GroupDoc
    {
        [JsonPropertyName("normalized_base_name")] public string NormalizedBaseName { get; init; } = "";
        [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
        [JsonPropertyName("members")] public MemberDoc[] Members { get; init; } = [];
    }

    private sealed class MemberDoc
    {
        [JsonPropertyName("path")] public string Path { get; init; } = "";
    }

    private sealed class DupDoc
    {
        [JsonPropertyName("hash")] public string Hash { get; init; } = "";
        [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
        [JsonPropertyName("files")] public string[] Files { get; init; } = [];
    }

    private sealed class ConflictDoc
    {
        [JsonPropertyName("normalized_base_name")] public string NormalizedBaseName { get; init; } = "";
        [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
        [JsonPropertyName("files")] public CFileDoc[] Files { get; init; } = [];
    }

    private sealed class CFileDoc
    {
        [JsonPropertyName("path")] public string Path { get; init; } = "";
        [JsonPropertyName("hash")] public string Hash { get; init; } = "";
    }

    private sealed class PhDoc
    {
        [JsonPropertyName("path")] public string Path { get; init; } = "";
        [JsonPropertyName("kinds")] public string[] Kinds { get; init; } = [];
        [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
    }
}
