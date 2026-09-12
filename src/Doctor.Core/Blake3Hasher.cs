namespace Doctor.Core;

using System.Buffers;

/// <summary>
/// Placeholder gate exception (ADR-0005 §6; contracts.md IHasher): thrown when
/// any content reading attempt targets a <see cref="FileEntry"/> marked as
/// placeholder (SPEC §6 — DO NOT TOUCH). Automated invariant: no placeholder
/// bytes are ever read — the gate precedes any opening/reading.
/// </summary>
// PlaceholderReadException: single source in PlaceholderGate.cs.

/// <summary>Hashing contract (docs/contracts.md — single source of types).</summary>
// IHasher and IStreamSource: single source in Hashing.cs (contracts.md).

/// <summary>
/// Product BLAKE3 hasher — exact v1 recipe from ADR-0005 (hash_version = 1 fixes
/// algorithm, window size and concatenation order):
///
///   • size ≤ 128 KiB (131072 B): single sequential read of the entire file in one
///     BLAKE3 hasher.
///   • size &gt; 128 KiB: a single opening; feeds the hasher with [0, 64 KiB) and then
///     [size − 64 KiB, size), in that order. The same bytes are never read twice
///     for the same hash.
///   • size == 0: zero-byte hash with NO reading.
///   • Lowercase hexadecimal output of 32 bytes (64 characters).
///
/// Determinism (contracts.md): the hash depends only on content; parallel reading never
/// changes the result. TOCTOU post-read revalidation belongs to the caller
/// (HashingPipeline, ADR-0005 §7); the cache consumes the same values (EPIC 04).
/// </summary>
public sealed class Blake3Hasher : IHasher
{
    /// <summary>v1 limit: up to this size (inclusive) the partial hash covers the entire file.</summary>
    public const int WholeFileLimitBytes = 128 * 1024;

    /// <summary>v1 window: first and last bytes of each large file.</summary>
    public const int WindowBytes = 64 * 1024;

    private const int CopyBufferSize = 256 * 1024;

    /// <summary>
    /// Canonical BLAKE3 hash of zero bytes (empty file, ADR-0005 §5). Stable and
    /// documented constant: no reading should happen to produce it.
    /// </summary>
    public static readonly string EmptyFileHash =
        Convert.ToHexString(Blake3.Hasher.Hash(Array.Empty<byte>()).AsSpan()).ToLowerInvariant();

    private readonly Func<FileEntry, Stream> _openRead;

    /// <summary>Injects a custom opener (tests with spy stream); production uses File.OpenRead.</summary>
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

    /// <summary>
    /// Full BLAKE3 hash by streaming (ADR-0005 §4; contracts.md IStreamSource):
    /// opens the file EXCLUSIVELY through the single content source and feeds a
    /// <see cref="Blake3.StreamingHasher"/> with 256 KiB buffers borrowed from
    /// <see cref="ArrayPool{T}"/> — the file is NEVER loaded entirely into memory,
    /// regardless of size. Same placeholder gate as
    /// <see cref="IHasher.FullHash"/>: refuses before any opening/reading.
    /// Lowercase hexadecimal output; byte-by-byte deterministic.
    /// </summary>
    public static string FullHashBlake3(IStreamSource streams, FileEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streams);
        GatePlaceholder(entry);

        using var stream = streams.OpenRead(entry);
        using var hasher = Blake3.Hasher.New();

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

        return ToHex(hasher.Finalize());
    }

    internal static void GatePlaceholder(FileEntry entry)
    {
        if (entry.IsPlaceholder)
        {
            // Refuses BEFORE any opening/reading — zero placeholder bytes read.
            throw new PlaceholderReadException(entry.Path);
        }
    }

    /// <summary>
    /// Recipe core over stream already opened by the caller. Internal so spy streams
    /// in tests can prove segment-by-segment the start+end pattern (HSH-01).
    /// </summary>
    internal static string ComputeHash(Stream stream, long declaredSize, bool partial, CancellationToken ct)
    {
        using var hasher = Blake3.Hasher.New();

        if (declaredSize == 0)
        {
            // ADR-0005 §5: zero-byte hash with no reading.
            return ToHex(hasher.Finalize());
        }

        if (!partial || declaredSize <= WholeFileLimitBytes)
        {
            // Single integral sequential read (partial for small files and complete).
            PumpSequential(stream, hasher, ct);
        }
        else
        {
            // Window [0, 64 KiB) and then [size − 64 KiB, size), in that order, in the SAME
            // opening and the SAME hasher — no re-reading of bytes (ADR-0005 §3).
            PumpWindow(stream, hasher, startOffset: 0, length: WindowBytes);
            PumpWindow(stream, hasher, startOffset: declaredSize - WindowBytes, length: WindowBytes);
        }

        return ToHex(hasher.Finalize());
    }

    /// <summary>Copies exactly <paramref name="length"/> bytes from the given offset, validating unexpected EOF.</summary>
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
                        $"unexpected EOF at offset {startOffset + (length - remaining)}: " +
                        "file smaller than the size declared at Level 0 (TOCTOU).");
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
        Convert.ToHexString(hash.AsSpan()).ToLowerInvariant(); // lowercase hex (ADR-0005 §1)
}
