using System.Text;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T24 (t_f37457ba) — CsvComparator (SPEC §16 CSV; ADR-0011 item 2; contracts.md
/// IDocumentComparator). RFC4180 subset parsing (double quotes, escaped quote "");
/// delimiter detected by counting in the first non-empty line of each file among
/// ',', ';', '\t'; DIFFERENT delimiters between files ⇒ ALL lines Changed
/// (structural difference). No header inference.
/// Alignment via <see cref="LcsDiff"/> on normalized RAW LINE; Changed pair
/// ⇒ cell-by-cell comparison: equal cells post-parse ⇒ region reverts to Equal
/// (redundant quotes are not semantic differences); different cell count ⇒ Changed
/// for entire line. No line reordering.
/// </summary>
[Trait("Category", "Comparison")]
public class CsvComparatorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _files = new();
    private int _seq;

    public CsvComparatorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t24-csv-").FullName;
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
    public void Csv01_Identical_EqualTrue_OneEqualRegion()
    {
        var (left, right) = CsvPair("a;b;c\n1;2;3\n");

        var result = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("csv", result.ComparatorKind);
        Assert.True(result.AreSemanticallyEqual);
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Equal, region.Kind);
    }

    [Fact]
    public void Csv02_SingleCellChanges_MinimalChangedPair()
    {
        // One modified cell in a 3-line table ⇒ ONE Changed region 1:1
        // on divergent line, surrounded by Equal blocks.
        var (left, right) = CsvPair(
            "colA;colB\none;two\nthree;four\n",
            "colA;colB\none;two\nthree;FOUR\n");

        var result = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(2, result.Regions.Count);
        Assert.Equal(RegionKind.Equal, result.Regions[0].Kind);
        Assert.Equal((0, 2, 0, 2), (result.Regions[0].LeftStart, result.Regions[0].LeftCount, result.Regions[0].RightStart, result.Regions[0].RightCount));
        var region = result.Regions[1];
        Assert.Equal(RegionKind.Changed, region.Kind);
        Assert.Equal((2, 1, 2, 1), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Csv03_LineAdded_Added()
    {
        var (left, right) = CsvPair("l1\nl2\n", "l1\nnew\nl2\n");

        var result = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(3, result.Regions.Count);
        var region = result.Regions[1];
        Assert.Equal(RegionKind.Added, region.Kind);
        Assert.Equal((1, 0, 1, 1), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Csv04_LineRemoved_Removed()
    {
        var (left, right) = CsvPair("l1\nold\nl2\n", "l1\nl2\n");

        var result = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(3, result.Regions.Count);
        var region = result.Regions[1];
        Assert.Equal(RegionKind.Removed, region.Kind);
        Assert.Equal((1, 1, 1, 0), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Csv05_ColumnCountDiffers_EntireLineChanged()
    {
        // Divergent raw pair whose post-parse cells have different counts:
        // stays Changed covering the entire line (never sub-line region).
        var (left, right) = CsvPair("one;two\n", "one;two;three\n");

        var result = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Changed, region.Kind);
        Assert.Equal((0, 1, 0, 1), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Csv06_RedundantQuotes_EqualCells_SemanticallyEqual()
    {
        // Cell-by-cell comparison: `"a";"b"` and `a;b` produce the SAME cells post-parse
        // ⇒ raw Changed region reverts to Equal and pair is semantically equal
        // (quotes are syntax, not content).
        var (left, right) = CsvPair("\"a\";\"b\"\n", "a;b\n");

        var result = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.True(result.AreSemanticallyEqual);
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Equal, region.Kind);
        Assert.Equal((0, 1, 0, 1), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Csv07_FieldsWithCommaEscapedQuotes_EqualWhenContentEqual()
    {
        // Comma and escaped quote "" inside quoted field: parser must preserve literal
        // content to declare true equality...
        const string content = "\"x,y\";\"he said \"\"hi\"\"\"\n";
        var (left, right) = CsvPair(content);

        var resultEqual = new CsvComparator().Compare(left, right, CancellationToken.None);
        Assert.True(resultEqual.AreSemanticallyEqual);

        // ...and detect REAL differences inside quoted field as Changed.
        var (l2, r2) = CsvPair(
            "\"x,y\";z\n",
            "\"x,z\";z\n");
        var resultDifferent = new CsvComparator().Compare(l2, r2, CancellationToken.None);

        Assert.False(resultDifferent.AreSemanticallyEqual);
        Assert.Equal(RegionKind.Changed, Assert.Single(resultDifferent.Regions).Kind);
    }

    [Fact]
    public void Csv08_DifferentDelimitersBetweenFiles_AllLinesChanged()
    {
        // ',' on left, ';' on right: structural difference ⇒ NO Equal region;
        // all lines on both sides fall into Changed regions.
        var (left, right) = CsvPair("a,b\n1,2\n", "x;y\n3;4\n");

        var result = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("csv", result.ComparatorKind);
        Assert.False(result.AreSemanticallyEqual);
        Assert.DoesNotContain(result.Regions, r => r.Kind == RegionKind.Equal);
        Assert.Equal(2, result.Regions.Sum(r => r.LeftCount));
        Assert.Equal(2, result.Regions.Sum(r => r.RightCount));
        Assert.All(result.Regions, r => Assert.Equal(RegionKind.Changed, r.Kind));
    }

    [Fact]
    public void Csv09_DelimiterDetectedOnFirstNonEmptyLine_InitialBlankIgnored()
    {
        // Detection looks at FIRST NON-EMPTY line: initial blank on left
        // cannot reduce count to zero (which would pick default ',' and trigger all-Changed
        // against right's ';').
        // Correct path: both ';' ⇒ normal diff of lines with Equal block.
        var (left, right) = CsvPair("\nk1;k2\nv1;v2\n", "k1;k2\nv1;V2\n");

        var result = new CsvComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(3, result.Regions.Count);
        // Blank line exists only on left ⇒ Removed; k1;k2 matches (Equal);
        // v1;v2 vs v1;V2 is Changed pair with divergent cells.
        Assert.Equal(RegionKind.Removed, result.Regions[0].Kind);
        Assert.Equal(RegionKind.Equal, result.Regions[1].Kind);
        Assert.Equal((1, 1, 0, 1), (result.Regions[1].LeftStart, result.Regions[1].LeftCount, result.Regions[1].RightStart, result.Regions[1].RightCount));
        Assert.Equal(RegionKind.Changed, result.Regions[2].Kind);
    }

    [Fact]
    public void Csv10_Placeholder_ExceptionBeforeAnyOpen()
    {
        // Inherited gate from 06.1 (ADR-0011 item 4): ghost path — any opening
        // would throw FileNotFoundException; receiving PlaceholderReadException proves zero
        // opens/zero bytes read.
        var ghost = Path.Combine(_dir, "not-existing.csv");
        var existing = Path.Combine(_dir, "real.csv");
        File.WriteAllBytes(existing, Encoding.UTF8.GetBytes("a;b\n"));
        _files.Add(existing);

        var ex = Assert.Throws<PlaceholderReadException>(() => new CsvComparator().Compare(
            TextComparatorTests.Entry(ghost, 10, placeholder: true),
            TextComparatorTests.Entry(existing, new FileInfo(existing).Length),
            CancellationToken.None));
        Assert.Equal(ghost, ex.EntryPath);

        var ex2 = Assert.Throws<PlaceholderReadException>(() => new CsvComparator().Compare(
            TextComparatorTests.Entry(existing, new FileInfo(existing).Length),
            TextComparatorTests.Entry(ghost, 10, placeholder: true),
            CancellationToken.None));
        Assert.Equal(ghost, ex2.EntryPath);
    }

    [Fact]
    public void Csv11_SameInputTwoExecutions_IdenticalResult()
    {
        var (left, right) = CsvPair(
            "a;b\none;two\nthree;four\n",
            "a;b\none;TWO\nthree;four\nextra\n");

        var cmp = new CsvComparator();
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

    /// <summary>Pair of .csv files with UTF-8 content without BOM.</summary>
    private (FileEntry Left, FileEntry Right) CsvPair(string contentLeft, string? contentRight = null)
    {
        var bytesLeft = Encoding.UTF8.GetBytes(contentLeft);
        var bytesRight = Encoding.UTF8.GetBytes(contentRight ?? contentLeft);
        var pl = Path.Combine(_dir, $"L{_seq}.csv");
        var pr = Path.Combine(_dir, $"R{_seq}.csv");
        _seq++;
        File.WriteAllBytes(pl, bytesLeft);
        File.WriteAllBytes(pr, bytesRight);
        _files.Add(pl);
        _files.Add(pr);
        return (
            TextComparatorTests.Entry(pl, bytesLeft.LongLength),
            TextComparatorTests.Entry(pr, bytesRight.LongLength));
    }
}
