using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T17 (t_70329e55) — Path canon and hostile names (SPEC §35 Security agent /
/// GATE 5 "path/reparse attacks tested"; ADR-0002 fail-closed; threat-model,
/// path traversal section).
///
/// Required card matrix: 12 hostile cases rejected by
/// <see cref="CanonicalPath.IsValidName"/> and 5 valid cases accepted, followed by
/// comprehensive sweep of NTFS reserved names (CON, PRN, AUX, NUL, COM1-9,
/// LPT1-9) in the three forms Windows recognizes (bare, lowercase with extension,
/// capitalized) and the contract of <see cref="CanonicalPath.Normalize"/>
/// (separator canonicalization, extension truncation > 255 chars, and comparison
/// by <see cref="StringComparer.OrdinalIgnoreCase"/>).
/// </summary>
public class CanonicalPathTests
{
    // ---- Card matrix: exactly 12 hostile names, all rejected ------

    [Theory]
    [InlineData(null)]                       // null
    [InlineData("")]                         // empty
    [InlineData("report\u0001.txt")]         // embedded control character
    [InlineData("photos//vacation.jpg")]     // // sequence in middle
    [InlineData("a/../b.txt")]               // .. in middle
    [InlineData("../escape.txt")]            // .. at start
    [InlineData("backup\\..\\target.txt")]   // .. with Windows separator
    [InlineData("CON")]                      // bare NTFS reserved
    [InlineData("prn.txt")]                  // reserved + extension, lowercase
    [InlineData("aux.mp3")]                  // reserved + extension
    [InlineData("NUL")]                      // bare NTFS reserved
    [InlineData("lpt7.xlsx")]                // LPT range + extension
    public void IsValidName_HostileName_Rejects(string? path)
    {
        Assert.False(CanonicalPath.IsValidName(path));
    }

    // ---- Card matrix: exactly 5 valid names, all accepted ---------

    [Theory]
    [InlineData("report.xlsx")]
    [InlineData("folder/file.txt")]                          // nested relative
    [InlineData("Beach photo.JPG")]                          // spaces + case
    [InlineData("conference.backup.sb-a3f19c.docx")]         // stem "con…" is not reserved
    [InlineData("data.2026/v2/notes.md")]                    // multiple dotted segments
    public void IsValidName_ValidName_Accepts(string path)
    {
        Assert.True(CanonicalPath.IsValidName(path));
    }

    // ---- Comprehensive sweep of NTFS reserved table --------------------------
    // The complete table has 22 names. Each enters in the three forms Windows treats as reserved:
    // bare, lowercase with extension, and capitalized with extension.

    public static IEnumerable<object[]> ReservedNamesWithVariations()
    {
        string[] bases =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        foreach (var name in bases)
        {
            yield return new object[] { name };                                  // CON
            yield return new object[] { name.ToLowerInvariant() + ".txt" };      // con.txt
            yield return new object[] { name[..1].ToUpperInvariant() + name[1..].ToLowerInvariant() + ".dat" }; // Con.dat, Prn.dat...
        }
    }

    [Theory]
    [MemberData(nameof(ReservedNamesWithVariations))]
    public void IsValidName_CompleteReservedTable_Rejects(string path)
    {
        Assert.False(CanonicalPath.IsValidName(path));
    }

    // ---- Boundaries reinforcing the stem before first dot rule ----------

    [Theory]
    [InlineData("CON/spreadsheet.xlsx")]            // reserved directory segment also applies
    [InlineData("folder/CON.txt")]                   // reserved as last segment
    [InlineData("CON.")]                            // trailing dot: stem remains reserved
    public void IsValidName_ReservedInAnySegment_Rejects(string path)
    {
        Assert.False(CanonicalPath.IsValidName(path));
    }

    // NTFS sanitization: Win32 discards trailing dot/space on creation
    // ("file.txt." becomes "file.txt") — path confusion vector;
    // canon fails closed instead of approving names the volume rewrites.

    [Theory]
    [InlineData("file.txt.")]
    [InlineData("file.txt ")]
    [InlineData("folder/notes .")]
    public void IsValidName_EndsWithDotOrSpace_Rejects(string path)
    {
        Assert.False(CanonicalPath.IsValidName(path));
    }

    [Theory]
    [InlineData("content.txt")]                     // reserved prefix is not reserved
    [InlineData("nullify.bin")]
    [InlineData("auxiliary.csv")]
    [InlineData("com10.txt")]                       // outside COM1-9 range
    [InlineData("lpt0.log")]                        // outside LPT1-9 range
    [InlineData("a/b/conference.txt")]
    [InlineData("documents CON/sheet.xlsx")]        // reserved is EXACT name, not substring
    public void IsValidName_SimilarButNotReserved_Accepts(string path)
    {
        Assert.True(CanonicalPath.IsValidName(path));
    }

    // ---- Normalize: contract ---------------------------------------------------

    [Fact]
    public void Normalize_WindowsSeparators_CanonicalizesToUnixSlash()
    {
        Assert.Equal("Folder/Sub/File.TXT", CanonicalPath.Normalize("Folder\\Sub\\File.TXT"));
    }

    [Fact]
    public void Normalize_ResultsCompareByOrdinalIgnoreCase()
    {
        // Contract: canonical key equality is case-insensitive
        // (NTFS semantics), always ORDINAL — no locale (SPEC §3).
        // Distinct from PathOrder.Comparer (report sorting, Ordinal sensitive).
        var a = CanonicalPath.Normalize("Notes/Final.TXT");
        var b = CanonicalPath.Normalize("notes/final.txt");

        Assert.Equal(a, b, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(a, b, StringComparer.Ordinal); // canon preserves original case
    }

    [Fact]
    public void Normalize_ExtensionAbove255_TruncatesTo255AndGateApproves()
    {
        var hostile = "file." + new string('a', 300);

        // Gate fails closed: does not approve component NTFS wouldn't store...
        Assert.False(CanonicalPath.IsValidName(hostile));

        // ...and Normalizer is the repair path: truncates extension to 255.
        var canon = CanonicalPath.Normalize(hostile);

        Assert.StartsWith("file.", canon);
        var extension = canon[(canon.LastIndexOf('.') + 1)..];
        Assert.Equal(255, extension.Length);
        Assert.True(CanonicalPath.IsValidName(canon));
    }

    [Fact]
    public void Normalize_Hostile_FailsClosedWithArgumentException()
    {
        // Never silently sanitizes traversal or reserved: throws.
        string? nullStr = null;
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize(nullStr!));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize(""));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize("a//b"));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize("x/../y"));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize("CON"));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize("lockup\u0007.txt"));
    }

    [Fact]
    public void Normalize_OutputPassesGate_ForFiveValidCardCases()
    {
        string[] validCases =
        {
            "report.xlsx",
            "folder/file.txt",
            "Beach photo.JPG",
            "conference.backup.sb-a3f19c.docx",
            "data.2026/v2/notes.md",
        };

        foreach (var name in validCases)
        {
            var canon = CanonicalPath.Normalize(name);
            Assert.True(CanonicalPath.IsValidName(canon));
        }
    }
}
