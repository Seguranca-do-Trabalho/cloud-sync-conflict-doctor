using System.Text;

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
/// Modelo de domínio mínimo consumido pela GUI — em forma do schema do relatório
/// v1 (docs/schema-report-v1.md), que prevalece sobre o exemplo do ADR-0003.
/// Modelos DEMO locais da GUI até o GATE 2 fechar; os nomes dos campos seguem o schema.
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

    /// <summary>
    /// Espaço recuperável: soma dos tamanhos dos itens elegíveis para quarentena —
    /// as perdedoras das duplicatas idênticas (todas menos a mantida, menor caminho
    /// em bytes UTF-8) mais as versões que NÃO serão mantidas nos conflitos reais
    /// (sugestão determinística mtime → size → path). A fórmula é testada explicitamente.
    /// </summary>
    public long RecoverableBytes =>
        IdenticalDuplicates.Sum(g =>
            (long)(g.Files.Count - 1)
            * g.SizeBytes)
        + RealConflicts.Sum(g => g.SumVersionsBytes() - SuggestVersionToKeep(g).SizeBytes);

    /// <summary>Sugestão determinística (SPEC §17): mtime → size → path em bytes UTF-8.</summary>
    internal static ConflictVersion SuggestVersionToKeep(ConflictGroup group) =>
        group.Versions
            .OrderByDescending(v => v.MtimeUtc.UtcTicks)
            .ThenByDescending(v => v.SizeBytes)
            .ThenBy(v => Encoding.UTF8.GetBytes(v.Path),
                Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b.AsSpan())))
            .First();

    /// <summary>
    /// Valida as invariantes normativas da telemetria do schema v1 §5.
    /// Vazio = conformidade; qualquer item = violação nomeada pelo contador.
    /// </summary>
    internal IEnumerable<string> ValidateTelemetryInvariants()
    {
        var t = Telemetry;

        if (t.FilesEnumerated != t.FilesPlaceholder + t.FilesSkipped + t.FilesPartialHashed)
        {
            yield return $"files_enumerated ({t.FilesEnumerated}) ≠ files_placeholder " +
                         $"({t.FilesPlaceholder}) + files_skipped ({t.FilesSkipped}) + " +
                         $"files_partial_hashed ({t.FilesPartialHashed})";
        }

        if (t.FilesFullHashed > t.FilesPartialHashed)
        {
            yield return $"files_full_hashed ({t.FilesFullHashed}) > files_partial_hashed " +
                         $"({t.FilesPartialHashed})";
        }

        if (t.BytesRead != t.BytesReadPartial + t.BytesReadFull)
        {
            yield return $"bytes_read ({t.BytesRead}) ≠ bytes_read_partial " +
                         $"({t.BytesReadPartial}) + bytes_read_full ({t.BytesReadFull})";
        }

        if (t.PlaceholderBytesRead != 0)
        {
            yield return $"placeholder_bytes_read ({t.PlaceholderBytesRead}) ≠ 0 — " +
                         "violação de segurança: placeholder jamais é aberto";
        }
    }
}

/// <summary>
/// Telemetria com os 9 contadores EXATOS do schema v1 §5, nesta ordem declarada:
/// files_enumerated, files_skipped, files_placeholder, files_partial_hashed,
/// files_full_hashed, bytes_read, bytes_read_partial, bytes_read_full,
/// placeholder_bytes_read. Contadores inteiros ≥ 0, largura de 64 bits.
/// </summary>
public sealed class ScanTelemetry
{
    /// <summary>Total de entradas vistas no Level 0 (diretórios nunca contam). Inclui placeholders.</summary>
    public long FilesEnumerated { get; init; }

    /// <summary>Não-placeholder que não teve nenhum byte lido (grupos unitários).</summary>
    public long FilesSkipped { get; init; }

    /// <summary>Entradas classificadas como placeholder e excluídas de todo acesso a conteúdo.</summary>
    public long FilesPlaceholder { get; init; }

    /// <summary>Receberam hash parcial no Level 2 (janelas inicial/final de 64 KiB).</summary>
    public long FilesPartialHashed { get; init; }

    /// <summary>Sobreviveram ao Level 2 e receberam BLAKE3 completo no Level 3.</summary>
    public long FilesFullHashed { get; init; }

    /// <summary>bytes_read_partial + bytes_read_full.</summary>
    public long BytesRead { get; init; }

    /// <summary>Bytes lidos no passe parcial; arquivo ≤ 128 KiB conta o arquivo inteiro aqui.</summary>
    public long BytesReadPartial { get; init; }

    /// <summary>Bytes lidos durante o hash completo (Level 3).</summary>
    public long BytesReadFull { get; init; }

    /// <summary>Regra absoluta §6/§7: sempre 0. Valor ≠ 0 é falha de segurança, não dado.</summary>
    public long PlaceholderBytesRead { get; init; }
}

public sealed class DuplicateGroup
{
    /// <summary>BLAKE3 completo, 64 hex minúsculos (schema v1 §6.1); DEMO usa prefixo demo-.</summary>
    public string Blake3Hash { get; init; } = "";
    public long SizeBytes { get; init; }
    /// <summary>Ordenado por caminho em bytes UTF-8 (regra de determinismo §3 / schema §1.3).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

public sealed class ConflictGroup
{
    /// <summary>Caminho base normalizado do grupo (schema v1 §6.2).</summary>
    public string BaseName { get; init; } = "";

    /// <summary>Tamanho comum do grupo (schema v1 §6.2); versões divergentes podem diferir dele na DEMO.</summary>
    public long TotalBytes { get; init; }

    public IReadOnlyList<ConflictVersion> Versions { get; init; } = [];

    /// <summary>Soma dos bytes de todas as versões — insumo direto da fórmula de espaço recuperável.</summary>
    public long SumVersionsBytes() => Versions.Sum(v => v.SizeBytes);
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

    /// <summary>Rótulos canônicos do schema v1 §6.3, ex.: recall_on_open, offline.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>Tamanho obtido por metadados de enumeração (Level 0), sem abrir o arquivo.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Motivo legível derivado dos rótulos (compatibilidade com o esqueleto T06).</summary>
    public string Reason { get; init; } = "";
}
