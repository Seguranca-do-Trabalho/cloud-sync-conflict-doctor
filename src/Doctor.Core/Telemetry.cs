using System.Text;

namespace Doctor.Core;

/// <summary>
/// Contrato de telemetria interna do scanner (SPEC §10) — a métrica principal do
/// produto é a quantidade de bytes que NÃO precisaram ser lidos.
///
/// Campos na ordem EXATA fixada pelo orquestrador (card T06): os oito contadores da
/// SPEC §10 em snake_case, seguidos do gate placeholder_bytes_read exigido por
/// docs/benchmark-harness.md §6 (rodada reprova se != 0). A emissão JSON determinística
/// segue esta ordem; alterá-la é mudança de contrato e exige registro em ADR novo.
///
/// Semântica normativa:
///   files_enumerated       = files_placeholder + files_skipped + files_partial_hashed
///   files_full_hashed     &lt;= files_partial_hashed
///   bytes_read             = bytes_read_partial + bytes_read_full
///   placeholder_bytes_read = 0 sempre (valor != 0 é violação de segurança, não dado)
/// </summary>
public sealed record ScanTelemetry
{
    /// <summary>Total de entradas de arquivo vistas no Level 0 (diretórios não contam). Inclui placeholders.</summary>
    public long FilesEnumerated { get; init; }

    /// <summary>Entradas não-placeholder que não tiveram NENHUM byte lido.</summary>
    public long FilesSkipped { get; init; }

    /// <summary>Entradas classificadas como placeholder e excluídas de todo acesso a conteúdo.</summary>
    public long FilesPlaceholder { get; init; }

    /// <summary>
    /// Entradas excluídas da enumeração por estarem sob a subárvore reservada
    /// &lt;raiz&gt;/ConflictDoctor/ (quarentena §18/SPEC; adendo T-15, SEG-12): política
    /// estrutural da fronteira canônica Level 0, não erro — nunca entra em Errors nem
    /// em qualquer contador de arquivos (R10; idempotência §20 entre rescans).
    /// </summary>
    public long FilesExcludedConflictDoctor { get; init; }

    /// <summary>Entradas não-placeholder que receberam hash parcial (janela inicial + janela final).</summary>
    public long FilesPartialHashed { get; init; }

    /// <summary>Entradas que sobreviveram ao hash parcial e receberam BLAKE3 completo. Subconjunto de FilesPartialHashed.</summary>
    public long FilesFullHashed { get; init; }

    /// <summary>Soma dos bytes efetivamente lidos: BytesReadPartial + BytesReadFull.</summary>
    public long BytesRead => checked(BytesReadPartial + BytesReadFull);

    /// <summary>Bytes lidos no passe de hash parcial (arquivos ≤ 128 KiB são lidos integralmente nesta conta).</summary>
    public long BytesReadPartial { get; init; }

    /// <summary>Bytes lidos no passe de hash completo.</summary>
    public long BytesReadFull { get; init; }

    /// <summary>Gate absoluto (benchmark-harness.md §6): bytes lidos de placeholders. SEMPRE 0; valor != 0 reprova a rodada.</summary>
    public long PlaceholderBytesRead { get; init; }

    /// <summary>
    /// Agrega os contadores de outro snapshot neste (ex.: shards de uma mesma rodada).
    /// Soma todos os contadores; bytes_read permanece derivado das parcelas e o gate
    /// placeholder_bytes_read só permanece zero se for zero em AMBOS os operandos.
    /// </summary>
    public ScanTelemetry Merge(ScanTelemetry other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new ScanTelemetry
        {
            FilesEnumerated = checked(FilesEnumerated + other.FilesEnumerated),
            FilesSkipped = checked(FilesSkipped + other.FilesSkipped),
            FilesPlaceholder = checked(FilesPlaceholder + other.FilesPlaceholder),
            FilesPartialHashed = checked(FilesPartialHashed + other.FilesPartialHashed),
            FilesFullHashed = checked(FilesFullHashed + other.FilesFullHashed),
            BytesReadPartial = checked(BytesReadPartial + other.BytesReadPartial),
            BytesReadFull = checked(BytesReadFull + other.BytesReadFull),
            PlaceholderBytesRead = checked(PlaceholderBytesRead + other.PlaceholderBytesRead),
        };
    }
}
