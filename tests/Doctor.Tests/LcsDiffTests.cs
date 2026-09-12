using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_64c4d4c0) — LCS engine over line lists (SPEC §16; ADR-0011 item 5:
/// own LCS, no external dependency; §52 anti-overengineering).
///
/// Output contract (orchestrator decision): sequence of <see cref="DiffRegion"/>
/// that PARTITIONS both documents in order — sum of LeftCount == left.Count,
/// sum of RightCount == right.Count, regions at increasing positions and non-
/// overlapping. Equal covers matched spans; Added = right only; Removed =
/// left only; Changed = substitute pair (one removed line paired 1:1 with one
/// added line in the same block).
/// </summary>
[Trait("Category", "Comparison")]
public class LcsDiffTests
{
    [Fact]
    public void Lcs01_BothEmpty_NoRegions()
    {
        var regions = LcsDiff.Diff([], []);

        Assert.Empty(regions);
    }

    [Fact]
    public void Lcs02_Identical_SingleEqualRegionCoveringAll()
    {
        var lines = new[] { "a", "b", "c" };

        var regions = LcsDiff.Diff(lines, lines);

        var region = Assert.Single(regions);
        Assert.Equal(RegionKind.Equal, region.Kind);
        Assert.Equal(0, region.LeftStart);
        Assert.Equal(3, region.LeftCount);
        Assert.Equal(0, region.RightStart);
        Assert.Equal(3, region.RightCount);
    }

    [Fact]
    public void Lcs03_Prefix_EqualFollowedByAdded()
    {
        var left = new[] { "a", "b" };
        var right = new[] { "a", "b", "c", "d" };

        var regions = LcsDiff.Diff(left, right);

        Assert.Equal(2, regions.Count);
        Assert.Equal(RegionKind.Equal, regions[0].Kind);
        Assert.Equal((0, 2, 0, 2), (regions[0].LeftStart, regions[0].LeftCount, regions[0].RightStart, regions[0].RightCount));
        Assert.Equal(RegionKind.Added, regions[1].Kind);
        Assert.Equal((2, 0, 2, 2), (regions[1].LeftStart, regions[1].LeftCount, regions[1].RightStart, regions[1].RightCount));
    }

    [Fact]
    public void Lcs04_Suffix_AddedFollowedByEqual()
    {
        var left = new[] { "c", "d" };
        var right = new[] { "a", "b", "c", "d" };

        var regions = LcsDiff.Diff(left, right);

        Assert.Equal(2, regions.Count);
        Assert.Equal(RegionKind.Added, regions[0].Kind);
        Assert.Equal((0, 0, 0, 2), (regions[0].LeftStart, regions[0].LeftCount, regions[0].RightStart, regions[0].RightCount));
        Assert.Equal(RegionKind.Equal, regions[1].Kind);
        Assert.Equal((0, 2, 2, 2), (regions[1].LeftStart, regions[1].LeftCount, regions[1].RightStart, regions[1].RightCount));
    }

    [Fact]
    public void Lcs05_PureRemoval_RegionRemoved()
    {
        var left = new[] { "a", "x", "b" };
        var right = new[] { "a", "b" };

        var regions = LcsDiff.Diff(left, right);

        Assert.Equal(3, regions.Count);
        Assert.Equal(RegionKind.Equal, regions[0].Kind);
        Assert.Equal(RegionKind.Removed, regions[1].Kind);
        Assert.Equal((1, 1, 1, 0), (regions[1].LeftStart, regions[1].LeftCount, regions[1].RightStart, regions[1].RightCount));
        Assert.Equal(RegionKind.Equal, regions[2].Kind);
        Assert.Equal((2, 1, 1, 1), (regions[2].LeftStart, regions[2].LeftCount, regions[2].RightStart, regions[2].RightCount));
    }

    [Fact]
    public void Lcs06_Interleaved_SubstitutionBecomesChanged()
    {
        var left = new[] { "line1", "alpha", "line3" };
        var right = new[] { "line1", "beta", "line3" };

        var regions = LcsDiff.Diff(left, right);

        Assert.Equal(3, regions.Count);
        Assert.Equal(RegionKind.Equal, regions[0].Kind);
        Assert.Equal((0, 1, 0, 1), (regions[0].LeftStart, regions[0].LeftCount, regions[0].RightStart, regions[0].RightCount));
        Assert.Equal(RegionKind.Changed, regions[1].Kind);
        Assert.Equal((1, 1, 1, 1), (regions[1].LeftStart, regions[1].LeftCount, regions[1].RightStart, regions[1].RightCount));
        Assert.Equal(RegionKind.Equal, regions[2].Kind);
        Assert.Equal((2, 1, 2, 1), (regions[2].LeftStart, regions[2].LeftCount, regions[2].RightStart, regions[2].RightCount));
    }

    [Fact]
    public void Lcs07_LargeSynthetic_FixedSeed_PartitionAndLcsAgainstOracle()
    {
        // Generator with FIXED seed (no seedless Random allowed).
        var rnd = new Random(42);
        var left = new List<string>(400);
        var right = new List<string>(400);
        for (int i = 0; i < 400; i++)
        {
            var line = $"L{i:D4}";
            left.Add(line);
            right.Add(rnd.Next(4) == 0 ? line + "-mut" : line);
        }

        // Spurious insertions and deletions make the case non-trivial.
        right.Insert(97, "inserted-97");
        right.Insert(233, "inserted-233");
        left.RemoveAt(151);

        var regions = LcsDiff.Diff(left, right);

        // Partition invariant: covers both documents completely, in order.
        int sumLeft = 0, sumRight = 0, lastLeft = 0, lastRight = 0;
        foreach (var r in regions)
        {
            Assert.Equal(lastLeft, r.LeftStart);
            Assert.Equal(lastRight, r.RightStart);
            lastLeft += r.LeftCount;
            lastRight += r.RightCount;
            sumLeft += r.LeftCount;
            sumRight += r.RightCount;
            if (r.Kind == RegionKind.Changed)
            {
                Assert.Equal(r.LeftCount, r.RightCount);
            }
        }

        Assert.Equal(left.Count, sumLeft);
        Assert.Equal(right.Count, sumRight);

        // Independent oracle: classic O(n·m) DP of LCS LENGTH.
        int expectedLcs = LcsLengthOracle(left, right);
        int equalLines = regions.Where(r => r.Kind == RegionKind.Equal).Sum(r => r.LeftCount);
        Assert.Equal(expectedLcs, equalLines);

        // Equal regions truly match line by line (Ordinal).
        foreach (var r in regions.Where(r => r.Kind == RegionKind.Equal))
        {
            for (int k = 0; k < r.LeftCount; k++)
            {
                Assert.Equal(left[r.LeftStart + k], right[r.RightStart + k]);
            }
        }
    }

    [Fact]
    public void Lcs08_SameInputTwoExecutions_IdenticalResult()
    {
        var left = new[] { "a", "x", "b", "y", "c" };
        var right = new[] { "a", "p", "b", "q", "c", "extra" };

        var first = LcsDiff.Diff(left, right);
        var second = LcsDiff.Diff(left, right);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i], second[i]);
        }
    }

    /// <summary>Classic LCS length DP — oracle independent of the Hirschberg engine.</summary>
    private static int LcsLengthOracle(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (int i = 1; i <= a.Count; i++)
        {
            for (int j = 1; j <= b.Count; j++)
            {
                dp[i, j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        return dp[a.Count, b.Count];
    }
}
