namespace Doctor.Core;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

/// <summary>
/// Fingerprint de conteúdo do <see cref="ScanResult"/> (card T19; consolida S11-4
/// e a regra R9 do threat-model — operation_id nunca depende do relógio):
///
///   operation_id = BLAKE3("ccd-scanresult-op-v1" || serialização canônica v1)
///                  truncado a 64 bits, hex minúscula (16 caracteres).
///
/// A serialização canônica percorre o grafo em ordem FIXA — listas ordenadas por
/// caminho canônico (<see cref="PathOrder"/>) e campos na ordem declarada abaixo —
/// de modo que DOIS ScanResults com o mesmo CONTEÚDO produzem o mesmo id,
/// independentemente da ordem física com que as listas foram construídas
/// (determinismo §3). Qualquer mudança de conteúdo (um hash, um membro, um size)
/// muda o id: ele é FUNÇÃO do conteúdo. Campos derivados ou informativos
/// (<c>BytesRead</c>, telemetria) não entram — só o que define o veredito.
/// </summary>
public static class ScanFingerprint
{
    /// <summary>Prefixo de domínio da receita v1 do operation_id.</summary>
    internal const string DomainTag = "ccd-scanresult-op-v1";

    /// <summary>
    /// Deriva o operation_id do CONTEÚDO do scan. Puro e determinístico:
    /// mesma árvore classificada ⇒ mesmo id; relógio não participa (R9).
    /// </summary>
    public static string OperationId(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var hasher = Blake3.Hasher.New();
        hasher.Update(Encoding.ASCII.GetBytes(DomainTag));

        // Grupos candidatos — ordem canônica por primeiro membro.
        foreach (var g in result.Groups.OrderBy(g => g.Members.FirstOrDefault(), PathOrder.Comparer))
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

        // Duplicatas idênticas — ordem por hash (Ordinal); membros em ordem canônica.
        foreach (var d in result.IdenticalDuplicates
                     .OrderBy(d => d.Hash, StringComparer.Ordinal)
                     .ThenBy(d => d.Files.FirstOrDefault(), PathOrder.Comparer))
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

        // Conflitos reais — ordem por nome base normalizado (Ordinal);
        // membros pelo par (caminho, hash) em ordem canônica.
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

        // Truncação a 64 bits (big-endian) ⇒ 16 hex minúsculas.
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

    /// <summary>Campo com comprimento prefixado (sem ambiguidade de concatenação).</summary>
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
