namespace Doctor.Core;

using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Strongly-typed cache key (threat-model T-08, rule R8; SPEC §12).
///
/// Represents the composite identity of a cache entry:
///   (volume_serial, file_id, size, mtime_ticks, algorithm, hash_version)
///
/// Never allows lookup by file_id alone — equality only matches when
/// ALL fields match. ToString() produces readable hex (for logs) without
/// exposing the absolute path.
/// </summary>
public sealed record VolumeFileKey(
    string VolumeSerial,
    string FileId,
    long Size,
    long MtimeTicks,
    string Algorithm,
    int HashVersion)
{
    /// <summary>
    /// Derives a canonical hex identifier (64 chars) from the key.
    /// Format: BLAKE3("ccd-vfk-v1" || volume_serial || file_id || size_BE || mtime_BE || algorithm || hash_version_BE).
    /// </summary>
    public string ToHex()
    {
        using var hasher = Blake3.Hasher.New();
        hasher.Update(Encoding.ASCII.GetBytes("ccd-vfk-v1"));
        hasher.Update(Encoding.UTF8.GetBytes(VolumeSerial));
        hasher.Update(Encoding.UTF8.GetBytes(FileId));

        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, Size);
        hasher.Update(buf);
        BinaryPrimitives.WriteInt64BigEndian(buf, MtimeTicks);
        hasher.Update(buf);
        hasher.Update(Encoding.UTF8.GetBytes(Algorithm));
        BinaryPrimitives.WriteInt32BigEndian(buf, HashVersion);
        hasher.Update(buf);

        return Convert.ToHexString(hasher.Finalize().AsSpan()).ToLowerInvariant();
    }

    /// <summary>Readable string for logs (does not expose path).</summary>
    public override string ToString() =>
        $"{VolumeSerial}:{FileId}|s{Size}|m{MtimeTicks}|{Algorithm}@{HashVersion}";
}
