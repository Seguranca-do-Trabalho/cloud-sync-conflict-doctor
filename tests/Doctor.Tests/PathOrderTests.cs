using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T07 — Ordem canônica do produto: bytes UTF-8 do caminho (StringComparer.Ordinal),
/// nunca locale nem OrdinalIgnoreCase (SPEC §3, ADR-0003, docs/contratos.md PathOrder).
/// </summary>
public class PathOrderTests
{
    private static FileEntry Entry(string path) => new()
    {
        Path = path,
        Size = 1,
        MtimeUtc = DateTimeOffset.UnixEpoch,
        Attributes = FileAttributes.Normal,
        VolumeId = "v",
        FileId = "f",
    };

    [Fact]
    public void Sort_ByteWiseOrdinal_AdversarialNames()
    {
        // Ordinal byte-a-byte: 'B'(0x42) < '_'(0x5F) < 'Z'(0x5A)? NÃO — 'Z' = 0x5A < '_' = 0x5F.
        // Ordem esperada por bytes UTF-8: 'B' < 'Z' < '_' < 'a' < 'a-agudo'.
        var input = new[] { "a", "_", "B", "á", "Z" };
        var expected = new[] { "B", "Z", "_", "a", "á" };

        var sorted = input.Select(Entry).OrderBy(e => e, PathOrder.Comparer).Select(e => e.Path).ToArray();

        Assert.Equal(expected, sorted);
        // Prova de que a escolha NÃO é OrdinalIgnoreCase (que daria 'a' antes de 'B').
        Assert.NotEqual(
            input.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            sorted);
    }
}
