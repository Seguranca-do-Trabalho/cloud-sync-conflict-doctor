namespace Doctor.Core;

/// <summary>
/// Hashing contract (docs/contracts.md — single source of types). Receives the
/// <see cref="FileEntry"/> from Level 0; implementations NEVER open placeholders
/// (SPEC §6). In production every implementation goes through
/// <see cref="PlaceholderGuardedHasher"/>, which throws
/// <see cref="PlaceholderReadException"/> for entries classified as
/// placeholder by <see cref="PlaceholderPolicy"/> (ADR-0005 item 6).
/// </summary>
public interface IHasher
{
    /// <summary>Partial hash (ADR-0005): ≤128 KiB whole file; larger, windows
    /// [0,64KiB) + [size−64KiB,size) in a single opening.</summary>
    string PartialHash(FileEntry entry, CancellationToken ct = default);

    /// <summary>Full BLAKE3 hash. Same placeholder gate.</summary>
    string FullHash(FileEntry entry, CancellationToken ct = default);
}

/// <summary>
/// Single source of product content opening: every legitimate hasher reads bytes
/// exclusively through here. Exists to make observable ("zero streams
/// opened over placeholder") and to centralize future restricted handle flags
/// (threat-model T-04/R4). Implementations do not decide on placeholders — that is
/// the exclusive responsibility of <see cref="PlaceholderPolicy"/> before the call.
/// </summary>
public interface IStreamSource
{
    /// <summary>Opens the file from <paramref name="entry"/> read-only.
    /// Legitimate callers arrive here only after the hasher gate.</summary>
    Stream OpenRead(FileEntry entry);
}
