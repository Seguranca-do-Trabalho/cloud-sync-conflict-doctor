using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_64c4d4c0) / T24 (t_f37457ba) — Comparator selection by extension, case-insensitive
/// (ADR-0011 item 1; contracts.md IDocumentComparator):
/// .txt/.log/.ini/.cfg/.conf ⇒ text; .md ⇒ markdown; .csv ⇒ csv; everything else,
/// including WITHOUT extension, ⇒ BinaryFallbackComparator.
/// </summary>
[Trait("Category", "Comparison")]
public class ComparatorSelectorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _files = new();

    public ComparatorSelectorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t23-sel-").FullName;
    }

    public void Dispose()
    {
        foreach (var a in _files)
        {
            File.Delete(a);
        }

        Directory.Delete(_dir);
    }

    [Theory]
    [InlineData(".TXT")]
    [InlineData(".txt")]
    [InlineData(".Log")]
    [InlineData(".INI")]
    [InlineData(".Cfg")]
    [InlineData(".CONF")]
    public void Sel01_TextExtension_CaseInsensitive_RoutesToTextComparator(string extension)
    {
        var (left, right) = PairWithExtension(extension);

        var result = ComparatorSelector.Compare(left, right, CancellationToken.None);

        Assert.Equal("text", result.ComparatorKind);
    }

    [Theory]
    [InlineData(".MD")]
    [InlineData(".md")]
    [InlineData(".Md")]
    public void Sel03_MarkdownExtension_RoutesToMarkdownComparator(string extension)
    {
        var (left, right) = PairWithExtension(extension);

        var result = ComparatorSelector.Compare(left, right, CancellationToken.None);

        Assert.Equal("markdown", result.ComparatorKind);
    }

    [Theory]
    [InlineData(".CSV")]
    [InlineData(".csv")]
    [InlineData(".Csv")]
    public void Sel04_CsvExtension_RoutesToCsvComparator(string extension)
    {
        var (left, right) = PairWithExtension(extension);

        var result = ComparatorSelector.Compare(left, right, CancellationToken.None);

        Assert.Equal("csv", result.ComparatorKind);
    }

    [Theory]
    [InlineData(".bin")]
    [InlineData(".docx")]
    [InlineData("")]
    public void Sel02_UnknownOrMissingExtension_RoutesToBinaryFallback(string extension)
    {
        var (left, right) = PairWithExtension(extension);

        var result = ComparatorSelector.Compare(left, right, CancellationToken.None);

        Assert.Equal("binary", result.ComparatorKind);
    }

    /// <summary>Creates identical pair of files with the specified extension.</summary>
    private (FileEntry Left, FileEntry Right) PairWithExtension(string extension)
    {
        var name = $"file{extension}";
        var pl = Path.Combine(_dir, $"L-{name}");
        var pr = Path.Combine(_dir, $"R-{name}");
        File.WriteAllBytes(pl, new byte[] { 0x61, 0x0A });
        File.WriteAllBytes(pr, new byte[] { 0x61, 0x0A });
        _files.Add(pl);
        _files.Add(pr);
        return (TextComparatorTests.Entry(pl, 2), TextComparatorTests.Entry(pr, 2));
    }
}
