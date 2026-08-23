namespace Doctor.Core;

using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Chave de cache tipada forte (threat-model T-08, regra R8; SPEC §12).
///
/// Representa a identidade composta de uma entrada de cache:
///   (volume_serial, file_id, size, mtime_ticks, algorithm, hash_version)
///
/// Nunca permite chavar por file_id sozinho — a igualdade só combina se
/// TODOS os campos baterem. O ToString() gera hex legível (para logs) sem
/// expor o caminho absoluto.
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
    /// Deriva um identificador hex canônico (64 chars) da chave.
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

    /// <summary>String legível para logs (não expõe caminho).</summary>
    public override string ToString() =>
        $"{VolumeSerial}:{FileId}|s{Size}|m{MtimeTicks}|{Algorithm}@{HashVersion}";
}
