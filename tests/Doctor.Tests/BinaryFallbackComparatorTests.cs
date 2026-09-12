using System.Text;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_64c4d4c0) — BinaryFallbackComparator (SPEC §16; ADR-0011 item 1: unknown
/// type ⇒ binary fallback pointing to first divergent bytes; contracts.md IDocumentComparator).
///
/// Orchestrator decision: fixed 64 KiB block reading; first divergence
/// ⇒ DiffRegion Changed with LeftStart=RightStart=offset and LeftCount=RightCount=1;
/// identical ⇒ AreSemanticallyEqual=true and empty Regions.
/// </summary>
[Trait("Category", "Comparison")]
public class BinaryFallbackComparatorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _files = new();
    private int _seq;

    public BinaryFallbackComparatorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t23-bin-").FullName;
    }

    public void Dispose()
    {
        foreach (var a in _files)
        {
            File.Delete(a);
        }

        Directory.Delete(_dir);
    }

    [Fact]
    public void Bin01_Identical_Equal_EmptyRegions()
    {
        var (left, right) = BytesPair(new byte[] { 0x00, 0x01, 0xFF, 0x7F }, new byte[] { 0x00, 0x01, 0xFF, 0x7F });

        var result = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("binary", result.ComparatorKind);
        Assert.True(result.AreSemanticallyEqual);
        Assert.Empty(result.Regions);
    }

    [Fact]
    public void Bin02_FirstDivergence_ChangedAtOffset_Count1()
    {
        var bytesL = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var bytesR = new byte[] { 0x01, 0x02, 0x99, 0x04 };
        var (left, right) = BytesPair(bytesL, bytesR);

        var result = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Changed, region.Kind);
        Assert.Equal((2, 1, 2, 1), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Bin03_FirstByte_DivergesAtOffsetZero()
    {
        var (left, right) = BytesPair(new byte[] { 0xAA, 0xBB }, new byte[] { 0xCC, 0xBB });

        var result = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        var region = Assert.Single(result.Regions);
        Assert.Equal((0, 1, 0, 1), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Bin04_FileLargerThanOneBlock_DivergenceInSecondBlock()
    {
        // 64 KiB blocks: divergence at offset 70_000 falls in the SECOND block —
        // proves block reading doesn't stop at the first block.
        const int offset = 70_000;
        var bytesL = new byte[80_000];
        var bytesR = new byte[80_000];
        Array.Fill(bytesL, (byte)0x42);
        Array.Fill(bytesR, (byte)0x42);
        bytesR[offset] = 0x24;

        var (left, right) = BytesPair(bytesL, bytesR);

        var result = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Changed, region.Kind);
        Assert.Equal((offset, 1, offset, 1), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Bin05_CommonPrefixEqual_DifferentSizes_SurplusIsUnilateralRegion()
    {
        // No divergent byte in common prefix, but different contents
        // (distinct sizes): surplus is reported as Removed (left only)
        // or Added (right only) — RegionKind vocabulary, orchestrator decision.
        var (left, right) = BytesPair(new byte[] { 0x01, 0x02, 0x03 }, new byte[] { 0x01, 0x02 });

        var result = new BinaryFallbackComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Removed, region.Kind);
        Assert.Equal((2, 1, 2, 0), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));

        var (left2, right2) = BytesPair(new byte[] { 0x01, 0x02 }, new byte[] { 0x01, 0x02, 0x03 });
        var result2 = new BinaryFallbackComparator().Compare(left2, right2, CancellationToken.None);
        var region2 = Assert.Single(result2.Regions);
        Assert.Equal(RegionKind.Added, region2.Kind);
        Assert.Equal((2, 0, 2, 1), (region2.LeftStart, region2.LeftCount, region2.RightStart, region2.RightCount));
    }

    [Fact]
    public void Bin06_Placeholder_ExceptionBeforeAnyOpen()
    {
        // Inherited gate (ADR-0011 item 4): ghost path — any opening
        // would throw FileNotFoundException; receiving PlaceholderReadException proves zero
        // placeholder bytes read.
        var ghost = Path.Combine(_dir, "not-existing.bin");
        var existing = Path.Combine(_dir, "real.bin");
        File.WriteAllBytes(existing, new byte[] { 0x01 });
        _files.Add(existing);

        var ex = Assert.Throws<PlaceholderReadException>(() => new BinaryFallbackComparator().Compare(
            Entry(ghost, 10, placeholder: true),
            Entry(existing, 1),
            CancellationToken.None));
        Assert.Equal(ghost, ex.EntryPath);
    }

    [Fact]
    public void Bin07_SameInputTwoExecutions_IdenticalResult()
    {
        var (left, right) = BytesPair(new byte[] { 0x00, 0xFF, 0x10 }, new byte[] { 0x00, 0xEE, 0x10 });

        var cmp = new BinaryFallbackComparator();
        var first = cmp.Compare(left, right, CancellationToken.None);
        var second = cmp.Compare(left, right, CancellationToken.None);

        Assert.Equal(first.ComparatorKind, second.ComparatorKind);
        Assert.Equal(first.AreSemanticallyEqual, second.AreSemanticallyEqual);
        Assert.Equal(first.Regions.Count, second.Regions.Count);
        for (int i = 0; i < first.Regions.Count; i++)
        {
            Assert.Equal(first.Regions[i], second.Regions[i]);
        }
    }

    /// <summary>Writes a pair of .bin files with raw bytes and returns L0 entries.</summary>
    private (FileEntry Left, FileEntry Right) BytesPair(byte[] bytesLeft, byte[] bytesRight)
    {
        var pl = Path.Combine(_dir, $"L{_seq}.bin");
        var pr = Path.Combine(_dir, $"R{_seq}.bin");
        _seq++;
        File.WriteAllBytes(pl, bytesLeft);
        File.WriteAllBytes(pr, bytesRight);
        _files.Add(pl);
        _files.Add(pr);
        return (Entry(pl, bytesLeft.LongLength), Entry(pr, bytesRight.LongLength));
    }

    /// <summary>Minimal L0 entry for comparison (non-placeholder).</summary>
    private static FileEntry Entry(string path, long size, bool placeholder = false) => new()
    {
        Path = path,
        Size = size,
        MtimeUtc = DateTimeOffset.UnixEpoch,
        Attributes = FileAttributes.Normal,
        VolumeId = "vol-test",
        FileId = path,
        IsPlaceholder = placeholder,
    };
}
