using System.Text;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_64c4d4c0) — TextComparator (SPEC §16 Text; ADR-0011 item 2; contracts.md
/// IDocumentComparator).
///
/// Normalization before diff (orchestrator decision): CRLF→LF; BOM (UTF-8/UTF-16)
/// recognized and ignored — divergent BOM ALONE does not make files different.
/// Exact line comparison post-normalization, no trim. Selection by extension
/// case-insensitive: .txt/.log/.ini/.cfg/.conf ⇒ text; rest ⇒ binary.
/// </summary>
[Trait("Category", "Comparison")]
public class TextComparatorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _files = new();
    private int _seq;

    public TextComparatorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t23-").FullName;
    }

    public void Dispose()
    {
        foreach (var f in _files)
        {
            File.Delete(f);
        }

        Directory.Delete(_dir);
    }

    [Fact]
    public void Txt01_Identical_EqualTrue_EmptyRegions()
    {
        var (left, right) = TextPair("l1\nl2\n", "l1\nl2\n");

        var result = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("text", result.ComparatorKind);
        Assert.True(result.AreSemanticallyEqual);
        // Engine contract: identical ⇒ ONE Equal region covering everything
        // (empty Regions is the BinaryFallbackComparator rule).
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Equal, region.Kind);
    }

    [Fact]
    public void Txt02_SingleDivergence_OneChangedRegionBetweenEquals()
    {
        var (left, right) = TextPair("a\nb\nc\nd\n", "a\nB\nc\nd\n");

        var result = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal(3, result.Regions.Count);
        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(RegionKind.Changed, result.Regions[1].Kind);
        Assert.Equal((1, 1, 1, 1), (result.Regions[1].LeftStart, result.Regions[1].LeftCount, result.Regions[1].RightStart, result.Regions[1].RightCount));
    }

    [Fact]
    public void Txt03_CrlfDifferOnlyAtEndOfLine_AreEqual()
    {
        // Same logical content: left with LF, right with CRLF. CRLF→LF normalization
        // precedes diff ⇒ equal.
        var (left, right) = BytesPair(
            Encoding.UTF8.GetBytes("l1\nl2\n"),
            Encoding.UTF8.GetBytes("l1\r\nl2\r\n"));

        var result = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.True(result.AreSemanticallyEqual);
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Equal, region.Kind);
    }

    [Fact]
    public void Txt04_DivergentBomAlone_DoesNotMakeDifferent()
    {
        // XML-doc contract: UTF-8 BOM recognized and ignored — presence/absence of
        // BOM alone does NOT produce a region.
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("x\ny\n")).ToArray();
        var withoutBom = Encoding.UTF8.GetBytes("x\ny\n");
        var (left, right) = BytesPair(withBom, withoutBom);

        var result = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.True(result.AreSemanticallyEqual);
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Equal, region.Kind);
    }

    [Fact]
    public void Txt05_EmptyFile_VsOneLine_Added()
    {
        var (left, right) = TextPair("", "single\n");

        var result = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Added, region.Kind);
        Assert.Equal((0, 0, 0, 1), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Txt06_NoTrim_TrailingSpaceIsDifference()
    {
        // Exact line comparison post-normalization: NO trim.
        var (left, right) = TextPair("value \n", "value\n");

        var result = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(RegionKind.Changed, Assert.Single(result.Regions).Kind);
    }

    [Fact]
    public void Txt07_Placeholder_ExceptionBeforeAnyOpen()
    {
        // Inherited gate (ADR-0011 item 4): the path DOES NOT EXIST on disk — any
        // open attempt would produce FileNotFoundException. The proof of
        // "zero bytes read / zero opens" is receiving PlaceholderReadException.
        var phantom = Path.Combine(_dir, "nonexistent.txt");
        var existing = Path.Combine(_dir, "real.txt");
        File.WriteAllBytes(existing, Encoding.UTF8.GetBytes("content\n"));
        _files.Add(existing);

        var ex = Assert.Throws<PlaceholderReadException>(() => new TextComparator().Compare(
            Entry(phantom, 10, placeholder: true),
            Entry(existing, new FileInfo(existing).Length),
            CancellationToken.None));
        Assert.Equal(phantom, ex.EntryPath);

        // Placeholder on the right side also blocks before reading the left.
        var ex2 = Assert.Throws<PlaceholderReadException>(() => new TextComparator().Compare(
            Entry(existing, new FileInfo(existing).Length),
            Entry(phantom, 10, placeholder: true),
            CancellationToken.None));
        Assert.Equal(phantom, ex2.EntryPath);
    }

    [Fact]
    public void Txt08_BomUtf16RecognizedAndIgnored_VsUtf8_Equal()
    {
        // UTF-16 LE BOM recognized by the decoder and ignored as a difference:
        // same logical content in different encodings ⇒ equal.
        const string content = "alpha\nbeta\n";
        var utf16Le = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(content)).ToArray();
        var utf8Bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray();
        var (left, right) = BytesPair(utf16Le, utf8Bom);

        var result = new TextComparator().Compare(left, right, CancellationToken.None);

        Assert.True(result.AreSemanticallyEqual);
    }

    [Fact]
    public void Txt09_SameInputTwoExecutions_IdenticalResult()
    {
        var (left, right) = TextPair("a\nb\nc\n", "a\nX\nc\nd\n");

        var cmp = new TextComparator();
        var first = cmp.Compare(left, right, CancellationToken.None);
        var second = cmp.Compare(left, right, CancellationToken.None);

        // ComparisonResult carries IReadOnlyList (reference equality) —
        // determinism is asserted field by field and region by region.
        Assert.Equal(first.ComparatorKind, second.ComparatorKind);
        Assert.Equal(first.AreSemanticallyEqual, second.AreSemanticallyEqual);
        Assert.Equal(first.Regions.Count, second.Regions.Count);
        for (int i = 0; i < first.Regions.Count; i++)
        {
            Assert.Equal(first.Regions[i], second.Regions[i]);
        }
    }

    /// <summary>Pair of .txt files with UTF-8 content without BOM.</summary>
    private (FileEntry Left, FileEntry Right) TextPair(string contentLeft, string contentRight) =>
        BytesPair(Encoding.UTF8.GetBytes(contentLeft), Encoding.UTF8.GetBytes(contentRight));

    /// <summary>Writes a pair of .txt files with raw bytes and returns L0 entries.</summary>
    private (FileEntry Left, FileEntry Right) BytesPair(byte[] bytesLeft, byte[] bytesRight)
    {
        var pl = Path.Combine(_dir, $"L{_seq}.txt");
        var pr = Path.Combine(_dir, $"R{_seq}.txt");
        _seq++;
        File.WriteAllBytes(pl, bytesLeft);
        File.WriteAllBytes(pr, bytesRight);
        _files.Add(pl);
        _files.Add(pr);
        return (Entry(pl, bytesLeft.LongLength), Entry(pr, bytesRight.LongLength));
    }

    /// <summary>Minimal L0 entry for comparison (non-placeholder).</summary>
    internal static FileEntry Entry(string path, long size, bool placeholder = false) => new()
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
