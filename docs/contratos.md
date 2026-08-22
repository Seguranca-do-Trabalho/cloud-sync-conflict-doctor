# Contratos de módulo — Cloud Sync Conflict Doctor

Contratos das interfaces-chave de `src/Doctor.Core`. Toda implementação deve respeitar a semântica de determinismo anotada. Namespace raiz: `Doctor.Core`. Regra transversal: **ordem canônica** = caminho em bytes UTF-8 (`StringComparer.Ordinal` sobre a forma normalizada do caminho); empate em qualquer decisão = `mtime` → `size` → `path` (ADR-0003).

```csharp
namespace Doctor.Core;

/// <summary>Ordem canônica de todo o produto: bytes UTF-8 do caminho; empate mtime → size → path.</summary>
public static class PathOrder
{
    public static int Compare(FileEntry a, FileEntry b);
    public static IOrderedEnumerable<T> Sort<T>(IEnumerable<T> items, Func<T, FileEntry> key);
}

/// <summary>Metadados coletados no Level 0. Imutável após criação.</summary>
public sealed record FileEntry
{
    public required string Path { get; init; }              // caminho completo normalizado
    public required long Size { get; init; }
    public required DateTimeOffset MtimeUtc { get; init; }
    public required FileAttributes Attributes { get; init; }
    public required string VolumeId { get; init; }
    public required string FileId { get; init; }            // NTFS file ID / equivalente
    public bool IsPlaceholder { get; init; }                // OFFLINE / RECALL_* / reparse point
    public PlaceholderKind? PlaceholderKind { get; init; }
}
```

## IFileEnumerator

```csharp
public interface IFileEnumerator
{
    /// <summary>Enumera metadados (Level 0) sem ler conteúdo. Nunca atravessa reparse points
    /// de diretório. A saída é SEMPRE ordenada por PathOrder antes de retornar —
    /// independente da ordem física do filesystem. Não lança por arquivo individual:
    /// erros de acesso vão para ScanErrors e não abortam o scan.</summary>
    EnumerationResult Enumerate(string rootPath, CancellationToken ct);
}

public sealed record EnumerationResult(
    IReadOnlyList<FileEntry> Files,      // ordenado por PathOrder — contrato
    IReadOnlyList<ScanError> Errors,
    ScanTelemetry Telemetry);
```

Determinismo: o implementador pode enumerar em qualquer ordem internamente, mas **deve** entregar a coleção ordenada por `PathOrder`; o pipeline jamais reordena por conta própria depois.

## IHasher

```csharp
public interface IHasher
{
    /// <summary>Hash parcial BLAKE3 (ADR-0005): arquivo ≤ 128 KiB inteiro;
    /// > 128 KiB janelas [0,64KiB) + [size-64KiB,size) numa única abertura.
    /// Lança PlaceholderReadException se entry.IsPlaceholder — invariantes: nenhum byte
    /// de placeholder é lido (placeholder_bytes_read == 0).</summary>
    string PartialHash(FileEntry entry, CancellationToken ct);

    /// <summary>Hash completo BLAKE3. Mesmo gate de placeholder.</summary>
    string FullHash(FileEntry entry, CancellationToken ct);
}
```

Determinismo: hash depende só do conteúdo; leitura paralela nunca altera o resultado. Revalidação TOCTOU pós-leitura (size+mtime) é responsabilidade do chamador via `ICacheStore`.

## ICacheStore

```csharp
public interface ICacheStore : IDisposable
{
    /// <summary>Hit somente com correspondência exata de (volume_id, file_id) + size + mtime
    /// + algorithm + hash_version (ADR-0006). Retorna null em qualquer divergência.</summary>
    CacheHit? Lookup(VolumeFileKey key, string algorithm, int hashVersion);

    /// <summary>Grava em lote dentro de uma única transação; crash deixa estado anterior intacto.</summary>
    void Store(IReadOnlyList<CacheEntry> entries);

    /// <summary>Poda idempotente de entradas last_seen anteriores ao cutoff.</summary>
    int Prune(DateTimeOffset cutoffUtc);
}
```

Determinismo: cache é otimização transparente — presença ou ausência de hit NUNCA muda o relatório emitido, apenas bytes lidos.

## IScanPipeline

```csharp
public interface IScanPipeline
{
    /// <summary>Executa L0→L1→L2→L3 (ADR-0004). Decisão serial sobre coleções ordenadas;
    /// apenas leitura/hash paralela (paralelismo via IConcurrencyPolicy).
    /// Saída única e versionada conforme ADR-0003.</summary>
    ScanReport Run(ScanRequest request, CancellationToken ct);
}

public sealed record ScanRequest(string RootPath, int ReadParallelism, bool UseCache);

public sealed record ScanReport
{
    public required int ReportSchemaVersion { get; init; }   // 1
    public required string Algorithm { get; init; }          // "BLAKE3"
    public required int HashVersion { get; init; }           // 1
    public required ScanTelemetry Telemetry { get; init; }   // inclui placeholder_bytes_read
    public required IReadOnlyList<GroupReport> Groups;       // ordenado PathOrder
    public required IReadOnlyList<IdenticalDuplicate> IdenticalDuplicates;
    public required IReadOnlyList<RealConflict> RealConflicts;
    public required IReadOnlyList<PlaceholderRecord> Placeholders;
}
```

Determinismo: `Run` é função pura da árvore + configuração declarada; mesmo input ⇒ relatório byte-a-byte idêntico (§3, §20), inclusive sob ordens de enumeração distintas.

## IReportWriter

```csharp
public interface IReportWriter
{
    /// <summary>Serializa o ScanReport exatamente no schema v1 (ADR-0003). Serialização
    /// determinística: propriedades em ordem fixa, listas pré-ordenadas pelo domínio,
    /// nenhuma timestamp incidental dentro das listas. Escrita atômica
    /// (tmp + rename) quando escreve em arquivo.</summary>
    void Write(ScanReport report, TextWriter output);
    void WriteToFile(ScanReport report, string path);
}
```

Determinismo: dois relatórios logicamente iguais produzem arquivos byte-idênticos; formatação numérica invariante à cultura (`InvariantCulture`).

## IQuarantine

```csharp
public interface IQuarantine
{
    /// <summary>Move itens para <raiz>/ConflictDoctor/quarantine/<op_id>/payload com manifesto
    /// atômico (ADR-0010). NUNCA apaga. Falha => status parcial honesto; nada silencioso.</summary>
    QuarantineResult Quarantine(IReadOnlyList<QuarantineItem> items, string rootPath, CancellationToken ct);

    /// <summary>Valida hash do payload e restaura. Destino ocupado => restaura com sufixo
    /// .restored-<op_id>; JAMAIS sobrescreve (ADR-0010 §4).</summary>
    RestoreResult Restore(string operationId, string rootPath, CancellationToken ct);
}
```

Determinismo: `<op_id>` = timestamp UTC + 8 hex derivados do conteúdo do manifesto — reproduzível, não aleatório (ADR-0010 §1).

## IDocumentComparator

```csharp
public interface IDocumentComparator
{
    /// <summary>Seleção por extensão case-insensitive; desconhecido => BinaryFallbackComparator
    /// (ADR-0011). Ambos os arquivos devem ser legíveis e não-placeholder (gate herdado do hasher).
    /// Resultado estruturado, ordenado, sem campo incidental de tempo.</summary>
    ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct);
}

public sealed record ComparisonResult(
    string ComparatorKind,                 // "text" | "markdown" | "csv" | "binary"
    bool AreSemanticallyEqual,
    IReadOnlyList<DiffRegion> Regions);    // ordenado por posição no documento
```

Determinismo: mesma dupla de arquivos ⇒ mesma `ComparisonResult`, sempre; a GUI apenas renderiza (§13).

## IMediaProbe

```csharp
public interface IMediaProbe
{
    /// <summary>Detecta seek penalty do volume que hospeda rootPath (ADR-0007).
    /// Implementação Windows-native via IOCTL_STORAGE_QUERY_PROPERTY; fakes nos testes.
    /// Indisponível/unknown => perfil conservador (parallelism baixo).</summary>
    ReadProfile ProbeVolume(string rootPath);
}

public sealed record ReadProfile(bool HasSeekPenalty, int RecommendedParallelism);
```

Determinismo: afeta apenas desempenho; nunca altera o conteúdo do relatório.

## IConcurrencyPolicy

```csharp
public interface IConcurrencyPolicy
{
    /// <summary>Resolve o paralelismo efetivo: flag > config > mídia > default conservador
    /// min(ProcessorCount, 4) (ADR-0007 §4). Valor >= 1 sempre.</summary>
    int ResolveReadParallelism(int? explicitFlag);
}
```

---

## Riscos principais e mitigações

| # | Risco | Impacto | Mitigação |
|---|---|---|---|
| R1 | Leitura acidental de placeholder (download induzido de árvore online-only) | Alto: custo de banda gigante, viola §6 | Gate em `IHasher` + `IsPlaceholder` em L0; teste automatizado `placeholder_bytes_read == 0` no CI (§21); enumeração não atravessa reparse points |
| R2 | Não-determinismo entre execuções (ordem de threads, locale, hash map) | Alto: quebra a promessa central do produto | Decisão serial sobre coleções ordenadas por `PathOrder`; serialização com cultura invariante; teste CI de 3 scans com ordens diferentes byte-idênticos (§20) |
| R3 | Perda/corrupção de dado na resolução | Crítico: confiança do produto morre | Nenhum delete em produção (ADR-0002); quarentena+manifesto atômico; restore valida hash e nunca sobrescreve; GATE 3 bloqueia downstream; revisão estática anti-delete |
| R4 | TOCTOU — arquivo muda entre L0 e leitura | Médio: hash atribuído a conteúdo errado | Revalidação size+mtime pós-leitura (ADR-0005 §7); persistindo divergência ⇒ erro parcial (exit 3), nunca silêncio |
| R5 | Cache servindo hash obsoleto | Médio: duplicata falsa/conflito perdido | Chave composta volume_id+file_id+size+mtime+algorithm+hash_version (ADR-0006); cache nunca promove parcial a completo; testes de invalidação |
| R6 | Normalização de conflito agressiva demais agrupando arquivos não relacionados | Médio: falsos positivos de conflito | Lista fixa pequena e versionada (`normalizer_version`); grupos são candidatos — decisão final sempre por hash real (L2/L3) |
| R7 | Paralelismo mal calibrado em HDD | Baixo: lentidão percebida | `IMediaProbe` abstrai seek penalty; default conservador; usuário pode fixar `--read-parallelism`; ajuste só com benchmark (§24) |
| R8 | Office semantic diff atrasar o v1 | Médio: prazo | Questão A isolada em card próprio; interface já comporta implementação futura sem refactor (ADR-0011) |
| R9 | Dependência NuGet comprometida (supply chain) | Médio: segurança | Versões pinadas em `Directory.Packages.props`; superfície mínima (Blake3, Microsoft.Data.Sqlite, System.CommandLine, Avalonia); revisão de dependências no GATE 5 |
| R10 | Caminhos longos/Unicode quebrando enumeração Windows | Médio: árvore parcialmente escaneada silenciosa | Erros por arquivo viram `ScanError` listado no relatório (nunca aborto silencioso); prefixo estendido `\\?\` quando aplicável |

Riscos R1–R3 são bloqueantes para seus gates respectivos (GATE 2 e GATE 3); os demais são monitorados por benchmark/review.
