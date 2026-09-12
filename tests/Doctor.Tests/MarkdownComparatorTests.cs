using System.Text;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T24 (t_f37457ba) — MarkdownComparator (SPEC §16 Markdown; ADR-0011 item 2;
/// contracts.md IDocumentComparator). Line-by-line textual diff over
/// <see cref="LcsDiff"/> with a LIGHT structural layer on top: ATX headings
/// (#{1..6}) OUTSIDE fenced code blocks and fence balance. Refinement rule:
/// non-Equal region whose span (left or right) contains a heading and whose heading
/// set differs between left/right ⇒ Kind=Changed. Common text-only differences
/// remain Added/Removed. No AST, no markdown parser.
/// </summary>
[Trait("Category", "Comparison")]
public class MarkdownComparatorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _files = new();
    private int _seq;

    public MarkdownComparatorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t24-md-").FullName;
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
    public void Md01_Identical_EqualTrue_OneEqualRegion()
    {
        var (left, right) = MarkdownPair("# Heading\ncommon text\n");

        var result = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("markdown", result.ComparatorKind);
        Assert.True(result.AreSemanticallyEqual);
        var region = Assert.Single(result.Regions);
        Assert.Equal(RegionKind.Equal, region.Kind);
    }

    [Fact]
    public void Md02_CommonTextChanged_SubstitutePair_RemainsChanged()
    {
        // Common text change (no heading in span) keeps the raw LCS
        // substitute pair: Changed 1:1 between two Equal.
        var (left, right) = MarkdownPair("intro\ncommon line one\nend\n", "intro\ncommon line TWO\nend\n");

        var result = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.Equal("markdown", result.ComparatorKind);
        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(3, result.Regions.Count);
        Assert.Equal(RegionKind.Changed, result.Regions[1].Kind);
        Assert.Equal((1, 1, 1, 1), (result.Regions[1].LeftStart, result.Regions[1].LeftCount, result.Regions[1].RightStart, result.Regions[1].RightCount));
    }

    [Fact]
    public void Md03_HeadingAdded_Refinement_BecomesChanged()
    {
        // Raw LCS would emit Added for the inserted heading; since the right span
        // contains a heading and the set differs (∅ vs {New chapter}), the region is
        // refined to Changed preserving the spans.
        var (left, right) = MarkdownPair("intro\nend\n", "intro\n# New chapter\nend\n");

        var result = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(3, result.Regions.Count);
        var region = result.Regions[1];
        Assert.Equal(RegionKind.Changed, region.Kind);
        Assert.Equal((1, 0, 1, 1), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Md04_HeadingRemoved_Refinement_BecomesChanged()
    {
        // Mirror of addition: pure heading removal becomes Changed by the same criterion.
        var (left, right) = MarkdownPair("intro\n# Chapter X\nend\n", "intro\nend\n");

        var result = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(3, result.Regions.Count);
        var region = result.Regions[1];
        Assert.Equal(RegionKind.Changed, region.Kind);
        Assert.Equal((1, 1, 1, 0), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
    }

    [Fact]
    public void Md05_HeadingModified_Changed()
    {
        var (left, right) = MarkdownPair("# Old heading\nbody\n", "# New heading\nbody\n");

        var result = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.True(result.Regions.Count >= 1);
        Assert.Contains(result.Regions, r => r.Kind == RegionKind.Changed);
        var first = result.Regions[0];
        Assert.Equal((0, 1, 0, 1), (first.LeftStart, first.LeftCount, first.RightStart, first.RightCount));
    }

    [Fact]
    public void Md06_HeadingLevelChanged_SetDiffers_Changed()
    {
        // "# T" and "## T" are distinct raw lines; same text after the hashes,
        // but the HEADING SET differs (level entered the set via the raw line).
        var (left, right) = MarkdownPair("# Same text\n", "## Same text\n");

        var result = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(RegionKind.Changed, Assert.Single(result.Regions).Kind);
    }

    [Fact]
    public void Md07_HeadingInsideBalancedFence_DoesNotCount()
    {
        // Apparent heading REMOVED from inside a CLOSED fence: it is not a
        // heading for the structural layer ⇒ the region remains Removed
        // (it would count as heading ⇒ become Changed in an implementation without fences).
        var (left, right) = MarkdownPair(
            "a\n```md\n# Only inside fence\n```\nb\n",
            "a\n```md\n```\nb\n");

        var result = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        // Structure: Equal(a + fence opening) | Removed(# inside fence) |
        // Equal(fence closing + b). The apparent heading does NOT become Changed.
        Assert.Equal(3, result.Regions.Count);
        Assert.Equal(RegionKind.Equal, result.Regions[0].Kind);
        var region = result.Regions[1];
        Assert.Equal(RegionKind.Removed, region.Kind);
        Assert.Equal((2, 1, 2, 0), (region.LeftStart, region.LeftCount, region.RightStart, region.RightCount));
        Assert.Equal(RegionKind.Equal, result.Regions[2].Kind);
    }

    [Fact]
    public void Md08_UnclosedFence_SubsequentLines_AreNotHeadings()
    {
        // OPENED fence never closed swallows all remaining document:
        // "# P1" and "# P2" in left are INSIDE it ⇒ no heading counts.
        // Both removal regions remain Removed; a reading that
        // ignored fences would transform removal of "# P2" into Changed.
        var (left, right) = MarkdownPair(
            "a\n```\n# P1\n# P2\nz\n",
            "a\n# P1\nz\n");

        var result = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(5, result.Regions.Count);
        Assert.Equal(RegionKind.Equal, result.Regions[0].Kind);
        Assert.Equal(RegionKind.Removed, result.Regions[1].Kind);
        Assert.Equal(RegionKind.Equal, result.Regions[2].Kind);
        Assert.Equal(RegionKind.Removed, result.Regions[3].Kind);
        Assert.Equal(RegionKind.Equal, result.Regions[4].Kind);
    }

    [Fact]
    public void Md09_CommonTextAdded_NoHeadingInSpan_RemainsAdded()
    {
        // Refinement does NOT trigger without heading: common text insertion stays Added.
        var (left, right) = MarkdownPair("a\nb\n", "a\nmiddle\nb\n");

        var result = new MarkdownComparator().Compare(left, right, CancellationToken.None);

        Assert.False(result.AreSemanticallyEqual);
        Assert.Equal(3, result.Regions.Count);
        Assert.Equal(RegionKind.Added, result.Regions[1].Kind);
        Assert.Equal((1, 0, 1, 1), (result.Regions[1].LeftStart, result.Regions[1].LeftCount, result.Regions[1].RightStart, result.Regions[1].RightCount));
    }

    [Fact]
    public void Md10_Placeholder_ExceptionBeforeAnyOpen()
    {
        // Gate inherited from 06.1 (ADR-0011 item 4): phantom path — any
        // open attempt would throw FileNotFoundException; receiving
        // PlaceholderReadException proves zero opens/zero bytes read.
        var phantom = Path.Combine(_dir, "nonexistent.md");
        var existing = Path.Combine(_dir, "real.md");
        File.WriteAllBytes(existing, Encoding.UTF8.GetBytes("# h\n"));
        _files.Add(existing);

        var ex = Assert.Throws<PlaceholderReadException>(() => new MarkdownComparator().Compare(
            TextComparatorTests.Entry(phantom, 10, placeholder: true),
            TextComparatorTests.Entry(existing, new FileInfo(existing).Length),
            CancellationToken.None));
        Assert.Equal(phantom, ex.EntryPath);

        var ex2 = Assert.Throws<PlaceholderReadException>(() => new MarkdownComparator().Compare(
            TextComparatorTests.Entry(existing, new FileInfo(existing).Length),
            TextComparatorTests.Entry(phantom, 10, placeholder: true),
            CancellationToken.None));
        Assert.Equal(phantom, ex2.EntryPath);
    }

    [Fact]
    public void Md11_SameInputTwoExecutions_IdenticalResult()
    {
        var (left, right) = MarkdownPair("# Heading\nintro\n# Section\nend\n", "# Heading\nintro\n## New section\n");

        var cmp = new MarkdownComparator();
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

    /// <summary>Pair of .md files with UTF-8 content without BOM.</summary>
    private (FileEntry Left, FileEntry Right) MarkdownPair(string contentLeft, string? contentRight = null)
    {
        var bytesLeft = Encoding.UTF8.GetBytes(contentLeft);
        var bytesRight = Encoding.UTF8.GetBytes(contentRight ?? contentLeft);
        var pl = Path.Combine(_dir, $"L{_seq}.md");
        var pr = Path.Combine(_dir, $"R{_seq}.md");
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
