using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T07 — Product canonical order: UTF-8 bytes of the path (StringComparer.Ordinal),
/// never locale nor OrdinalIgnoreCase (SPEC §3, ADR-0003, docs/contracts.md PathOrder).
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
        // Ordinal byte-by-byte: 'B'(0x42) < '_'(0x5F) < 'Z'(0x5A)? NO — 'Z' = 0x5A < '_' = 0x5F.
        // Expected order by UTF-8 bytes: 'B' < 'Z' < '_' < 'a' < 'a-acute'.
        var input = new[] { "a", "_", "B", "á", "Z" };
        var expected = new[] { "B", "Z", "_", "a", "á" };

        var sorted = input.Select(Entry).OrderBy(e => e, PathOrder.Comparer).Select(e => e.Path).ToArray();

        Assert.Equal(expected, sorted);
        // Proof that the choice is NOT OrdinalIgnoreCase (which would put 'a' before 'B').
        Assert.NotEqual(
            input.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            sorted);
    }
}
