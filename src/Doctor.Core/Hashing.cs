namespace Doctor.Core;

/// <summary>
/// Contrato de hashing (docs/contratos.md — fonte única de tipos). Recebe o
/// <see cref="FileEntry"/> do Level 0; implementações NUNCA abrem placeholder
/// (SPEC §6). Em produção toda implementação atravessa
/// <see cref="PlaceholderGuardedHasher"/>, que lança
/// <see cref="PlaceholderReadException"/> para entradas classificadas como
/// placeholder por <see cref="PlaceholderPolicy"/> (ADR-0005 item 6).
/// </summary>
public interface IHasher
{
    /// <summary>Hash parcial (ADR-0005): ≤128 KiB inteiro; maior, janelas
    /// [0,64KiB) + [size−64KiB,size) numa única abertura.</summary>
    string PartialHash(FileEntry entry, CancellationToken ct = default);

    /// <summary>Hash completo BLAKE3. Mesmo gate de placeholder.</summary>
    string FullHash(FileEntry entry, CancellationToken ct = default);
}

/// <summary>
/// Fonte única de abertura de conteúdo do produto: todo hasher legítimo lê bytes
/// exclusivamente através daqui. Existe para tornar observável ("zero streams
/// abertos sobre placeholder") e para concentrar flags futuras de handle restrito
/// (threat-model T-04/R4). Implementações não decidem sobre placeholders — isso é
/// papel exclusivo de <see cref="PlaceholderPolicy"/> antes da chamada.
/// </summary>
public interface IStreamSource
{
    /// <summary>Abre o arquivo de <paramref name="entry"/> somente-leitura.
    /// Chamadores legítimos chegam aqui apenas após o gate do hasher.</summary>
    Stream OpenRead(FileEntry entry);
}
