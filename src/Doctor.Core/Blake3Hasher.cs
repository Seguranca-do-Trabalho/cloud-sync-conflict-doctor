namespace Doctor.Core;

using System.Buffers;

/// <summary>
/// Exceção do gate de placeholder (ADR-0005 §6; contratos.md IHasher): lançada quando
/// qualquer tentativa de leitura de conteúdo mira uma <see cref="FileEntry"/> marcada
/// como placeholder (SPEC §6 — NÃO TOCAR). Invariante automatizada: nenhum byte de
/// placeholder é lido — o gate precede qualquer abertura/leitura.
/// </summary>
// PlaceholderReadException: fonte unica em PlaceholderGate.cs.

/// <summary>Contrato de hashing (docs/contratos.md — fonte única de tipos).</summary>
// IHasher e IStreamSource: fonte unica em Hashing.cs (contratos.md).

/// <summary>
/// Hasher BLAKE3 do produto — receita v1 EXATA do ADR-0005 (hash_version = 1 fixa
/// algoritmo, tamanho de janela e ordem de concatenação):
///
///   • size ≤ 128 KiB (131072 B): leitura sequencial única do arquivo inteiro em um
///     único hasher BLAKE3.
///   • size &gt; 128 KiB: UMA única abertura; alimenta o hasher com [0, 64 KiB) e depois
///     [size − 64 KiB, size), nesta ordem. Os mesmos bytes nunca são lidos duas vezes
///     para o mesmo hash.
///   • size == 0: hash de zero bytes sem NENHUMA leitura.
///   • Saída hexadecimal minúscula de 32 bytes (64 caracteres).
///
/// Determinismo (contratos.md): o hash depende só do conteúdo; leitura paralela nunca
/// altera o resultado. A revalidação TOCTOU pós-leitura pertence ao chamador
/// (HashingPipeline, ADR-0005 §7); o cache consome os mesmos valores (EPIC 04).
/// </summary>
public sealed class Blake3Hasher : IHasher
{
    /// <summary>Limite v1: até este tamanho (inclusive) o hash parcial cobre o arquivo inteiro.</summary>
    public const int WholeFileLimitBytes = 128 * 1024;

    /// <summary>Janela v1: primeiros e últimos bytes de cada arquivo grande.</summary>
    public const int WindowBytes = 64 * 1024;

    private const int CopyBufferSize = 256 * 1024;

    /// <summary>
    /// Hash BLAKE3 canônico de zero bytes (arquivo vazio, ADR-0005 §5). Constante
    /// estável e documentada: nenhuma leitura deve acontecer para produzi-la.
    /// </summary>
    public static readonly string EmptyFileHash =
        Convert.ToHexString(Blake3.Hasher.Hash(Array.Empty<byte>()).AsSpan()).ToLowerInvariant();

    private readonly Func<FileEntry, Stream> _openRead;

    /// <summary>Injeta um opener customizado (testes com stream espiã); produção usa File.OpenRead.</summary>
    public Blake3Hasher(Func<FileEntry, Stream>? openReadOverride = null) =>
        _openRead = openReadOverride ?? (static entry => File.OpenRead(entry.Path));

    /// <inheritdoc cref="IHasher.PartialHash"/>
    public string PartialHash(FileEntry entry, CancellationToken ct)
    {
        GatePlaceholder(entry);
        using var stream = _openRead(entry);
        return ComputeHash(stream, entry.Size, partial: true, ct);
    }

    /// <inheritdoc cref="IHasher.FullHash"/>
    public string FullHash(FileEntry entry, CancellationToken ct)
    {
        GatePlaceholder(entry);
        using var stream = _openRead(entry);
        return ComputeHash(stream, entry.Size, partial: false, ct);
    }

    internal static void GatePlaceholder(FileEntry entry)
    {
        if (entry.IsPlaceholder)
        {
            // Recusa ANTES de qualquer abertura/leitura — zero bytes de placeholder lidos.
            throw new PlaceholderReadException(entry.Path);
        }
    }

    /// <summary>
    /// Núcleo da receita sobre stream já aberto pelo chamador. Interno para as streams
    /// espiãs dos testes provarem segmento a segmento o padrão início+fim (HSH-01).
    /// </summary>
    internal static string ComputeHash(Stream stream, long declaredSize, bool partial, CancellationToken ct)
    {
        using var hasher = Blake3.Hasher.New();

        if (declaredSize == 0)
        {
            // ADR-0005 §5: hash de zero bytes sem nenhuma leitura.
            return ToHex(hasher.Finalize());
        }

        if (!partial || declaredSize <= WholeFileLimitBytes)
        {
            // Leitura sequencial única integral (parcial de arquivos pequenos e completo).
            PumpSequential(stream, hasher, ct);
        }
        else
        {
            // Janela [0, 64 KiB) e depois [size − 64 KiB, size), nesta ordem, na MESMA
            // abertura e no MESMO hasher — sem releitura de bytes (ADR-0005 §3).
            PumpWindow(stream, hasher, startOffset: 0, length: WindowBytes);
            PumpWindow(stream, hasher, startOffset: declaredSize - WindowBytes, length: WindowBytes);
        }

        return ToHex(hasher.Finalize());
    }

    /// <summary>Copia exatamente <paramref name="length"/> bytes do offset dado, validando EOF inesperado.</summary>
    private static void PumpWindow(Stream stream, Blake3.Hasher hasher, long startOffset, int length)
    {
        stream.Seek(startOffset, SeekOrigin.Begin);

        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(length, CopyBufferSize));
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, rented.Length);
                var read = stream.Read(rented, 0, chunk);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        $"EOF inesperado em offset {startOffset + (length - remaining)}: " +
                        "arquivo menor que o size declarado no Level 0 (TOCTOU).");
                }

                hasher.Update(rented.AsSpan(0, read));
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void PumpSequential(Stream stream, Blake3.Hasher hasher, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = stream.Read(rented, 0, rented.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Update(rented.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string ToHex(Blake3.Hash hash) =>
        Convert.ToHexString(hash.AsSpan()).ToLowerInvariant(); // hex minúscula (ADR-0005 §1)
}
