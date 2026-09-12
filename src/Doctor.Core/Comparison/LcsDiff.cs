namespace Doctor.Core;

/// <summary>
/// LCS-based diff engine over line sequences (SPEC §16; ADR-0011 item 5 —
/// own ~Hirschberg implementation, linear space O(n+m), time O(n·m), no
/// external dependency; §52). Post-normalization exact line comparison
/// (<see cref="StringComparison.Ordinal"/>, no trim).
///
/// Output: list of <see cref="DiffRegion"/> that PARTITIONS both documents in
/// order — sum(LeftCount) == left.Count, sum(RightCount) == right.Count,
/// ascending non-overlapping positions. Substitution blocks emit
/// <see cref="RegionKind.Changed"/> pairing the first min(d,r) lines 1:1,
/// followed by <see cref="RegionKind.Removed"/>/<see cref="RegionKind.Added"/>
/// for the excess. Deterministic: same input ⇒ same output (Hirschberg split
/// tie always by smallest index).
/// </summary>
public static class LcsDiff
{
    private const byte Keep = 0;
    private const byte Remove = 1;
    private const byte Insert = 2;

    /// <summary>Computes diff regions between <paramref name="left"/> and <paramref name="right"/>.</summary>
    public static List<DiffRegion> Diff(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var ops = new List<byte>(left.Count + right.Count);
        Align(left, right, 0, left.Count, 0, right.Count, ops);
        return ToRegions(ops);
    }

    /// <summary>
    /// Hirschberg divide &amp; conquer: splits <paramref name="left"/> in half,
    /// finds the optimal cut of <paramref name="right"/> by two DP passes with
    /// one row each and recurses. Base cases resolve directly (no n·m matrix
    /// allocation). Cut tie always by smallest index ⇒ stable output.
    /// </summary>
    private static void Align(
        IReadOnlyList<string> a, IReadOnlyList<string> b,
        int al, int ar, int bl, int br,
        List<byte> ops)
    {
        if (al == ar)
        {
            for (int k = bl; k < br; k++)
            {
                ops.Add(Insert);
            }

            return;
        }

        if (bl == br)
        {
            for (int k = al; k < ar; k++)
            {
                ops.Add(Remove);
            }

            return;
        }

        if (ar - al == 1)
        {
            string line = a[al];
            int k = bl;
            while (k < br && !string.Equals(b[k], line, StringComparison.Ordinal))
            {
                k++;
            }

            for (int x = bl; x < k; x++)
            {
                ops.Add(Insert);
            }

            if (k < br)
            {
                ops.Add(Keep);
                for (int x = k + 1; x < br; x++)
                {
                    ops.Add(Insert);
                }
            }
            else
            {
                // No match: the pre-loop already emitted ALL b lines as
                // Insert; only the single a line needs Remove.
                ops.Add(Remove);
            }

            return;
        }

        if (br - bl == 1)
        {
            string line = b[bl];
            int j = al;
            while (j < ar && !string.Equals(a[j], line, StringComparison.Ordinal))
            {
                j++;
            }

            for (int x = al; x < j; x++)
            {
                ops.Add(Remove);
            }

            if (j < ar)
            {
                ops.Add(Keep);
                for (int x = j + 1; x < ar; x++)
                {
                    ops.Add(Remove);
                }
            }
            else
            {
                // No match: the pre-loop already emitted ALL a lines as
                // Remove; only the single b line needs Insert.
                ops.Add(Insert);
            }

            return;
        }

        int mid = al + ((ar - al) >> 1);
        int m = br - bl;
        var forward = LcsForwardLine(a, al, mid, b, bl, br);
        var backward = LcsBackwardLine(a, mid, ar, b, bl, br);

        int best = -1;
        int cut = bl;
        for (int x = 0; x <= m; x++)
        {
            int sum = forward[x] + backward[x];
            if (sum > best)
            {
                best = sum;
                cut = bl + x;
            }
        }

        Align(a, b, al, mid, bl, cut, ops);
        Align(a, b, mid, ar, cut, br, ops);
    }

    /// <summary>
    /// DP line: result[x] = |LCS(a[ai,af), b[bi,bi+x))|. A single row of
    /// size m+1 alive per call — this is where linear space comes from.
    /// </summary>
    private static int[] LcsForwardLine(IReadOnlyList<string> a, int ai, int af, IReadOnlyList<string> b, int bi, int bf)
    {
        int m = bf - bi;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int i = ai; i < af; i++)
        {
            string line = a[i];
            cur[0] = 0;
            for (int x = 1; x <= m; x++)
            {
                cur[x] = string.Equals(line, b[bi + x - 1], StringComparison.Ordinal)
                    ? prev[x - 1] + 1
                    : Math.Max(prev[x], cur[x - 1]);
            }

            (prev, cur) = (cur, prev);
        }

        return prev;
    }

    /// <summary>
    /// Reverse DP mirror: result[x] = |LCS(a[ai,af), b[bi+x, bf))|.
    /// Allows matching the two halves at the optimal cut without a second n·m matrix.
    /// </summary>
    private static int[] LcsBackwardLine(IReadOnlyList<string> a, int ai, int af, IReadOnlyList<string> b, int bi, int bf)
    {
        int m = bf - bi;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int i = af - 1; i >= ai; i--)
        {
            string line = a[i];
            cur[m] = 0;
            for (int x = m - 1; x >= 0; x--)
            {
                cur[x] = string.Equals(line, b[bi + x], StringComparison.Ordinal)
                    ? prev[x + 1] + 1
                    : Math.Max(prev[x], cur[x + 1]);
            }

            (prev, cur) = (cur, prev);
        }

        return prev;
    }

    /// <summary>
    /// Converts the operation sequence into partitioned regions. Each maximal block
    /// of non-Keep operations becomes up to three regions: <see cref="RegionKind.Changed"/>
    /// (1:1 pair of the first min(d,i) lines), then the excess as
    /// <see cref="RegionKind.Removed"/> or <see cref="RegionKind.Added"/>.
    /// </summary>
    private static List<DiffRegion> ToRegions(List<byte> ops)
    {
        var regions = new List<DiffRegion>();
        int li = 0, ri = 0, i = 0;
        while (i < ops.Count)
        {
            if (ops[i] == Keep)
            {
                int start = i;
                while (i < ops.Count && ops[i] == Keep)
                {
                    i++;
                }

                int c = i - start;
                regions.Add(new DiffRegion(RegionKind.Equal, li, c, ri, c));
                li += c;
                ri += c;
                continue;
            }

            int leftStart = li, rightStart = ri, dels = 0, ins = 0;
            while (i < ops.Count && ops[i] != Keep)
            {
                if (ops[i] == Remove)
                {
                    dels++;
                    li++;
                }
                else
                {
                    ins++;
                    ri++;
                }

                i++;
            }

            int p = Math.Min(dels, ins);
            if (p > 0)
            {
                regions.Add(new DiffRegion(RegionKind.Changed, leftStart, p, rightStart, p));
            }

            if (dels > p)
            {
                regions.Add(new DiffRegion(RegionKind.Removed, leftStart + p, dels - p, rightStart + p, 0));
            }
            else if (ins > p)
            {
                regions.Add(new DiffRegion(RegionKind.Added, leftStart + p, 0, rightStart + p, ins - p));
            }
        }

        return regions;
    }
}
