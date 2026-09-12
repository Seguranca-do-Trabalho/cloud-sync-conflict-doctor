namespace Doctor.Core;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

/// <summary>
/// Content fingerprint of <see cref="ScanResult"/> (card T19; consolidates S11-4
/// and threat-model rule R9 — operation_id never depends on the clock):
///
///   operation_id = BLAKE3("ccd-scanresult-op-v1" || v1 canonical serialization)
///                  truncated to 64 bits, lowercase hex (16 characters).
///
/// Canonical serialization traverses the graph in FIXED order — lists sorted by
/// canonical path (<see cref="PathOrder"/>) and fields in the order declared below —
/// so that TWO ScanResults with the same CONTENT produce the same id,
/// regardless of the physical order lists were built
/// (determinism §3). Any content change (a hash, a member, a size)
/// changes the id: it is a FUNCTION of the content. Derived or informational
/// fields (<c>BytesRead</c>, telemetry) do not enter — only what defines the verdict.
/// </summary>
public static class ScanFingerprint
{
    /// <summary>v1 recipe operation_id domain prefix.</summary>
    internal const string DomainTag = "ccd-scanresult-op-v1";

    /// <summary>
    /// Derives the operation_id from scan CONTENT. Pure and deterministic:
    /// same classified tree ⇒ same id; clock does not participate (R9).
    /// </summary>
    public static string OperationId(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var hasher = Blake3.Hasher.New();
        hasher.Update(Encoding.ASCII.GetBytes(DomainTag));

        // Candidate groups — canonical order by first member.
        foreach (var g in result.Groups.OrderBy(g => g.Members.FirstOrDefault(), (IComparer<FileEntry?>)PathOrder.Comparer))
        {
            hasher.Update(Tag("g"));
            hasher.Update(Field(g.NormalizedBaseName));
            hasher.Update(FieldInt(g.SizeBytes));
            hasher.Update(FieldInt(g.Members.Count));
            foreach (var m in CanonicalMembers(g.Members))
            {
                HashEntry(hasher, m);
            }
        }

        // Identical duplicates — order by hash (Ordinal); members in canonical order.
        foreach (var d in result.IdenticalDuplicates
                     .OrderBy(d => d.Hash, StringComparer.Ordinal)
                     .ThenBy(d => d.Files.FirstOrDefault(), (IComparer<FileEntry?>)PathOrder.Comparer))
        {
            hasher.Update(Tag("i"));
            hasher.Update(Field(d.Hash));
            hasher.Update(FieldInt(d.SizeBytes));
            hasher.Update(FieldInt(d.Files.Count));
            foreach (var f in CanonicalFiles(d.Files))
            {
                HashEntry(hasher, f);
            }
        }

        // Real conflicts — order by normalized base name (Ordinal);
        // members by (path, hash) pair in canonical order.
        foreach (var c in result.RealConflicts
                     .OrderBy(c => c.NormalizedBaseName, StringComparer.Ordinal)
                     .ThenBy(c => c.SizeBytes))
        {
            hasher.Update(Tag("r"));
            hasher.Update(Field(c.NormalizedBaseName));
            hasher.Update(FieldInt(c.SizeBytes));
            hasher.Update(FieldInt(c.Files.Count));
            foreach (var m in c.Files.OrderBy(m => m.Path, StringComparer.Ordinal))
            {
                hasher.Update(Field(m.Path));
                hasher.Update(Field(m.Hash));
            }
        }

        // Truncation to 64 bits (big-endian) ⇒ 16 lowercase hex chars.
        var full = hasher.Finalize();
        Span<byte> digest = stackalloc byte[32];
        full.AsSpan().CopyTo(digest);
        var truncated = BinaryPrimitives.ReadUInt64BigEndian(digest[0..8]);
        return truncated.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<FileEntry> CanonicalFiles(IReadOnlyList<FileEntry> files) =>
        files.OrderBy(f => f, PathOrder.Comparer);

    private static IEnumerable<FileEntry> CanonicalMembers(IReadOnlyList<FileEntry> members) =>
        members.OrderBy(m => m, PathOrder.Comparer);

    private static void HashEntry(Blake3.Hasher hasher, FileEntry entry)
    {
        hasher.Update(Field(entry.Path));
        hasher.Update(FieldInt(entry.Size));
        hasher.Update(FieldInt(entry.MtimeUtc.UtcTicks));
        hasher.Update(Field(entry.VolumeId));
        hasher.Update(Field(entry.FileId));
    }

    /// <summary>Length-prefixed field (no concatenation ambiguity).</summary>
    private static ReadOnlySpan<byte> Tag(string tag) => Field(tag);

    private static byte[] Field(string value)
    {
        var payload = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var buf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, payload.Length);
        payload.CopyTo(buf, 4);
        return buf;
    }

    private static byte[] FieldInt(long value)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, value);
        return buf;
    }
}
