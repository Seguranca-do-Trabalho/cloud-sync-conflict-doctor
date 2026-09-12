using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T08 (t_6c210834) — Level 1 Grouping (SPEC §7; ADR-0004 §5; test-strategy §3.6).
/// Covered IDs: GRP-01 (fixed list with positive and negative cases per pattern),
/// GRP-02 (unknown suffix does not normalize; registered version),
/// GRP-03 (only same normalized name + size groups) and L1 determinism proof:
/// input order does not change output (structural rule ADR-0004 §1).
/// </summary>
public class GroupingTests
{
    private static FileEntry Entry(string path, long size) => new()
    {
        Path = path,
        Size = size,
        MtimeUtc = DateTimeOffset.UnixEpoch,
        Attributes = FileAttributes.Normal,
        VolumeId = "v",
        FileId = path,
    };

    // ---- GRP-01: each pattern from fixed list with positive case -----------------

    [Theory]
    [InlineData("relatorio (conflicted copy).xlsx", "relatorio.xlsx")]
    [InlineData("orcamento-DESKTOP-ABC123.xlsx", "orcamento.xlsx")]
    [InlineData("foto (1).jpg", "foto.jpg")]
    [InlineData("foto (2).jpg", "foto.jpg")]
    [InlineData("backup.txt~", "backup.txt")]
    [InlineData("~$curriculo.docx", "curriculo.docx")]
    [InlineData("backup.sb-a3f19c.txt", "backup.txt")]
    public void NormalizeBaseName_KnownPattern_ReducesToBaseName(string name, string expected)
    {
        Assert.Equal(expected, Grouping.NormalizeBaseName(name));
    }

    [Fact]
    public void NormalizeBaseName_CompositePatterns_FixpointWithinLimit()
    {
        // Real sync composition: Office lockfile + hostname + numbered copy.
        Assert.Equal(
            "Relatorio.xlsx",
            Grouping.NormalizeBaseName("~$Relatorio-DESKTOP-ABC123 (conflicted copy).xlsx"));
    }

    // ---- GRP-01: negative case per pattern (name without pattern remains untouched) ----

    [Theory]
    [InlineData("relatorio.xlsx")]                       // without " (conflicted copy)"
    [InlineData("relatorio (conflicted copy)v2.xlsx")]   // pattern not adjacent to extension
    [InlineData("orcamento.xlsx")]                       // without -DESKTOP-
    [InlineData("orcamento-SERVIDOR-ABC123.xlsx")]       // marker is "-DESKTOP-", other host does not match
    [InlineData("foto.jpg")]                             // without " (N)"
    [InlineData("backup.txt")]                           // without trailing ~
    [InlineData("curriculo.docx")]                       // without ~$ prefix
    public void NormalizeBaseName_WithoutPattern_NameIntact(string name)
    {
        Assert.Equal(name, Grouping.NormalizeBaseName(name));
    }

    // ---- GRP-02: unknown suffix does NOT normalize; registered version --------

    [Fact]
    public void NormalizeBaseName_SuffixOutsideList_DoesNotNormalize_AndVersionIsOne()
    {
        // " (3)" is NOT in the fixed list (only " (1)" and " (2)") — swallowing it would be
        // unbounded heuristic (SPEC §7; risk R6). The same applies to arbitrary suffixes.
        Assert.Equal("documento (3).pdf", Grouping.NormalizeBaseName("documento (3).pdf"));
        Assert.Equal("arquivo.bak2", Grouping.NormalizeBaseName("arquivo.bak2"));
        Assert.Equal("backup.sb-ZZ9.txt", Grouping.NormalizeBaseName("backup.sb-ZZ9.txt"));

        // Fixed list version recorded for report (normalizer_version).
        Assert.Equal(1, Grouping.NormalizerVersion);
    }

    // ---- GRP-03: only same normalized name + size groups ---------------------

    [Fact]
    public void Group_SameNameAndSameSize_Groups_OrderedByPath()
    {
        var groups = Grouping.Group(new[]
        {
            Entry("/r/docs/foto (1).jpg", 100),
            Entry("/r/fotos/foto.jpg", 100),
            Entry("/r/a/foto (conflicted copy).jpg", 100),
        });

        var group = Assert.Single(groups);
        Assert.Equal("foto.jpg", group.NormalizedBaseName);
        Assert.Equal(100, group.SizeBytes);
        Assert.Equal(
            new[] { "/r/a/foto (conflicted copy).jpg", "/r/docs/foto (1).jpg", "/r/fotos/foto.jpg" },
            group.Members.Select(m => m.Path).ToArray());
    }

    [Fact]
    public void Group_DifferentSizesOrDifferentBases_NeverGroup()
    {
        var groups = Grouping.Group(new[]
        {
            Entry("/r/foto.jpg", 100),
            Entry("/r/foto (1).jpg", 200),   // same base, different size
            Entry("/r/video.mp4", 100),      // same size, different base
            Entry("/r/single.txt", 5),       // single-element group does not become candidate
        });

        Assert.All(groups, g => Assert.True(g.Members.Count >= 2));
        Assert.Empty(groups);
    }

    // ---- Determinism in L1 scope (ADR-0004 §1): distinct input orders
    // ---- produce identical output; groups ordered by (base bytes, size). ------

    [Fact]
    public void Group_DistinctInputOrder_IdenticalOutput_AndCanonicalOrdering()
    {
        var entries = new[]
        {
            Entry("/r/z/notas.txt", 10),
            Entry("/r/z/notas (1).txt", 10),
            Entry("/r/a/orcamento.xlsx", 30),
            Entry("/r/m/orcamento (conflicted copy).xlsx", 30),
            Entry("/r/m/orcamento.xlsx", 70),          // same base, different size
            Entry("/r/A/notas.txt", 10),               // "A" < "z" in bytes (Ordinal)
        };

        var order1 = Grouping.Group(entries);
        var order2 = Grouping.Group(entries.Reverse());

        static List<string> Project(IReadOnlyList<ConflictGroup> groups) => groups
            .Select(g => $"{g.NormalizedBaseName}|{g.SizeBytes}|{string.Join(";", g.Members.Select(m => m.Path))}")
            .ToList();

        Assert.Equal(Project(order1), Project(order2));

        // Canonical group order: base in bytes, then ascending size.
        // Group ("orcamento.xlsx", 70) is SINGLE-ELEMENT and therefore does NOT appear (§6.0).
        Assert.Equal(
            new[] { ("notas.txt", 10L), ("orcamento.xlsx", 30L) },
            order1.Select(g => (g.NormalizedBaseName, g.SizeBytes)).ToArray());
        Assert.Equal(
            new[] { "/r/A/notas.txt", "/r/z/notas (1).txt", "/r/z/notas.txt" },
            order1[0].Members.Select(m => m.Path).ToArray());
    }
}
