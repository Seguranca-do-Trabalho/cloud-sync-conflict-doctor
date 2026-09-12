namespace Doctor.Tests;

using System.IO;
using System.Text;
using Doctor.Core;

public sealed class ReportWriterTests : IDisposable
{
    private const string FixtureTree = "fixtures/report-v1-tree";
    private readonly string? _fixtureJson;

    public ReportWriterTests()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory ?? ".",
            "..", "..", "fixtures", "report-v1-example.json"));
        _fixtureJson = File.Exists(fixturePath) ? File.ReadAllText(fixturePath, Encoding.UTF8) : null;
    }

    public void Dispose() { }

    [Fact]
    public void Write_SameInput_TwoWrites_ByteIdentical()
    {
        var (result, telemetry, placeholders, rootPath, started, finished) = QuickTestData();
        var bytes1 = Write(result, telemetry, placeholders, rootPath, started, finished);
        var bytes2 = Write(result, telemetry, placeholders, rootPath, started, finished);
        Assert.Equal(bytes1, bytes2);
    }

    [Fact]
    public void Write_VsFixture_ByteIdentical()
    {
        if (_fixtureJson is null) return;
        var (result, telemetry, placeholders, rootPath, started, finished) = FixtureData();
        var bytes = Write(result, telemetry, placeholders, rootPath, started, finished);
        var current = Encoding.UTF8.GetString(bytes);
        Assert.Equal(MaskTimestamps(_fixtureJson), MaskTimestamps(current));
    }

    [Fact]
    public void Write_KeyOrder_MatchesSchemaV1()
    {
        var (result, telemetry, placeholders, rootPath, started, finished) = QuickTestData();
        var json = Json(result, telemetry, placeholders, rootPath, started, finished);
        AssertRootOrder(json);
    }

    [Fact]
    public void Write_ExactFormat_UTF8NoBOM_LF_Final_NewlineTerminated()
    {
        var (result, telemetry, placeholders, rootPath, started, finished) = QuickTestData();
        var bytes = Write(result, telemetry, placeholders, rootPath, started, finished);
        Assert.NotEqual((byte)0xEF, bytes[0]);
        Assert.NotEqual((byte)0xBB, bytes[1]);
        Assert.NotEqual((byte)0xBF, bytes[2]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[bytes.Length - 1]);
        if (bytes.Length >= 2) Assert.NotEqual((byte)'\n', bytes[bytes.Length - 2]);
    }

    [Fact]
    public void Write_Telemetry_Included_WithCorrectValues()
    {
        var t = new ScanTelemetry
        {
            FilesEnumerated = 42, FilesPlaceholder = 3, FilesSkipped = 10,
            FilesPartialHashed = 29, FilesFullHashed = 20,
            BytesReadPartial = 100_000, BytesReadFull = 200_000, PlaceholderBytesRead = 0,
        };
        var (result, _, placeholders, rootPath, started, finished) = QuickTestData(t);
        var json = Json(result, t, placeholders, rootPath, started, finished);
        Assert.Contains("\"files_enumerated\": 42", json);
        Assert.Contains("\"files_placeholder\": 3", json);
        Assert.Contains("\"files_skipped\": 10", json);
        Assert.Contains("\"files_partial_hashed\": 29", json);
        Assert.Contains("\"files_full_hashed\": 20", json);
        Assert.Contains("\"bytes_read\": 300000", json);
        Assert.Contains("\"bytes_read_partial\": 100000", json);
        Assert.Contains("\"bytes_read_full\": 200000", json);
        Assert.Contains("\"placeholder_bytes_read\": 0", json);
    }

    [Fact]
    public void Write_Placeholders_SortedByPathBytes()
    {
        // The writer delegates ordering to the caller; tests verify that input
        // already comes sorted and that output preserves that order.
        var correctOrder = new List<PlaceholderRecord>
        {
            new() { Path = "aaa/first.bin", Kinds = ["recall_on_open"], SizeBytes = 2 },
            new() { Path = "mmm/middle.bin", Kinds = ["reparse_point"], SizeBytes = 3 },
            new() { Path = "zzz/last.bin", Kinds = ["offline"], SizeBytes = 1 },
        };
        var (_, telemetry, _, rootPath, started, finished) = QuickTestData();
        var result = new ScanResult([], [], []);
        var json = Json(result, telemetry, correctOrder, rootPath, started, finished);
        var idxAaa = json.IndexOf("\"aaa/first.bin\"");
        var idxMmm = json.IndexOf("\"mmm/middle.bin\"");
        var idxZzz = json.IndexOf("\"zzz/last.bin\"");
        Assert.True(idxAaa >= 0 && idxMmm > idxAaa && idxZzz > idxMmm);
    }

    [Fact]
    public void Write_Groups_SortedByNormalizedBaseAndSize()
    {
        // Grouping.Group already sorts in the pipeline; writer preserves order.
        var groupAlpha100 = new ConflictGroup("alpha.txt", 100L, Array.Empty<FileEntry>());
        var groupAlpha200 = new ConflictGroup("alpha.txt", 200L, Array.Empty<FileEntry>());
        var groupBeta = new ConflictGroup("beta.txt", 100L, Array.Empty<FileEntry>());
        // Canonical order already applied.
        var result = new ScanResult([groupAlpha100, groupAlpha200, groupBeta], [], []);
        var (_, telemetry, placeholders, rootPath, started, finished) = QuickTestData();
        var json = Json(result, telemetry, placeholders, rootPath, started, finished);
        var idxAlpha100 = json.IndexOf("\"normalized_base_name\": \"alpha.txt\"");
        Assert.True(idxAlpha100 >= 0);
        var idxSize100 = json.IndexOf("\"size_bytes\": 100", idxAlpha100);
        Assert.True(idxSize100 > idxAlpha100);
        var idxSize200 = json.IndexOf("\"size_bytes\": 200", idxSize100);
        Assert.True(idxSize200 > idxSize100);
        var idxBeta = json.IndexOf("\"normalized_base_name\": \"beta.txt\"");
        Assert.True(idxBeta > idxSize200);
    }

    [Fact]
    public void Write_GroupMembers_SortedByPathBytes()
    {
        // Group with members already sorted by path (preserves order).
        var entries = new[]
        {
            new FileEntry { Path = "/tmp/t13-test-root/a.txt", Size = 1, MtimeUtc = DateTimeOffset.UnixEpoch, Attributes = FileAttributes.Normal, VolumeId = "v", FileId = "1" },
            new FileEntry { Path = "/tmp/t13-test-root/m.txt", Size = 1, MtimeUtc = DateTimeOffset.UnixEpoch, Attributes = FileAttributes.Normal, VolumeId = "v", FileId = "2" },
            new FileEntry { Path = "/tmp/t13-test-root/z.txt", Size = 1, MtimeUtc = DateTimeOffset.UnixEpoch, Attributes = FileAttributes.Normal, VolumeId = "v", FileId = "3" },
        };
        var group = new ConflictGroup("test.txt", 1L, entries);
        var result = new ScanResult([group], [], []);
        var (_, telemetry, placeholders, rootPath, started, finished) = QuickTestData();
        var json = Json(result, telemetry, placeholders, rootPath, started, finished);
        var idxA = json.IndexOf("\"a.txt\"");
        var idxM = json.IndexOf("\"m.txt\"");
        var idxZ = json.IndexOf("\"z.txt\"");
        Assert.True(idxA >= 0 && idxM > idxA && idxZ > idxM);
    }

    // ---- Helpers ----------------------------------------------------------

    private static string Json(ScanResult r, ScanTelemetry t, IReadOnlyList<PlaceholderRecord> ph, string rp, DateTimeOffset s, DateTimeOffset f)
    {
        using var ms = new MemoryStream();
        new ReportWriterJson().Write(r, t, ph, rp, s, f, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static byte[] Write(ScanResult r, ScanTelemetry t, IReadOnlyList<PlaceholderRecord> ph, string rp, DateTimeOffset s, DateTimeOffset f)
    {
        using var ms = new MemoryStream();
        new ReportWriterJson().Write(r, t, ph, rp, s, f, ms);
        return ms.ToArray();
    }

    private static (ScanResult, ScanTelemetry, List<PlaceholderRecord>, string, DateTimeOffset, DateTimeOffset) QuickTestData(ScanTelemetry? telemetry = null)
    {
        var rootPath = "/tmp/t13-test-root";
        var started = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var finished = new DateTimeOffset(2026, 8, 23, 10, 0, 1, TimeSpan.Zero);
        var te = telemetry ?? new ScanTelemetry
        {
            FilesEnumerated = 5, FilesPlaceholder = 1, FilesSkipped = 1,
            FilesPartialHashed = 3, FilesFullHashed = 2,
            BytesReadPartial = 1024, BytesReadFull = 2048, PlaceholderBytesRead = 0,
        };
        var entries = new[]
        {
            new FileEntry { Path = Path.Combine(rootPath, "a.txt"), Size = 100, MtimeUtc = started, Attributes = FileAttributes.Normal, VolumeId = "v", FileId = "1" },
            new FileEntry { Path = Path.Combine(rootPath, "b.txt"), Size = 100, MtimeUtc = started, Attributes = FileAttributes.Normal, VolumeId = "v", FileId = "2" },
            new FileEntry { Path = Path.Combine(rootPath, "c.txt"), Size = 200, MtimeUtc = started, Attributes = FileAttributes.Normal, VolumeId = "v", FileId = "3" },
        };
        var groups = new[]
        {
            new ConflictGroup("a.txt", 100L, entries.Take(2).ToArray()),
            new ConflictGroup("b.txt", 200L, Array.Empty<FileEntry>()),
        };
        var identical = new[] { new IdenticalDuplicate("aaaa", 100L, entries.Take(2).ToArray()) };
        var conflicts = new[] { new RealConflict("b.txt", 200L, new[] { new ConflictMember(Path.Combine(rootPath, "c.txt"), "bbbb") }) };
        var placeholders = new List<PlaceholderRecord> { new() { Path = Path.Combine(rootPath, "p1.bin"), Kinds = ["offline"], SizeBytes = 512 } };
        return (new ScanResult(groups, identical, conflicts), te, placeholders, rootPath, started, finished);
    }

    private static (ScanResult, ScanTelemetry, List<PlaceholderRecord>, string, DateTimeOffset, DateTimeOffset) FixtureData()
    {
        var rootPath = FixtureTree;
        var started = new DateTimeOffset(2026, 8, 22, 19, 40, 0, 0, TimeSpan.Zero);
        var finished = new DateTimeOffset(2026, 8, 22, 19, 40, 0, 41, TimeSpan.Zero);
        var telemetry = new ScanTelemetry
        {
            FilesEnumerated = 10, FilesPlaceholder = 2, FilesSkipped = 1,
            FilesPartialHashed = 7, FilesFullHashed = 5,
            BytesReadPartial = 262632, BytesReadFull = 524864, PlaceholderBytesRead = 0,
        };
        var groups = new[]
        {
            new ConflictGroup("meeting-photo.jpg", 96L, Array.Empty<FileEntry>()),
            new ConflictGroup("budget.xlsx", 262144L, Array.Empty<FileEntry>()),
            new ConflictGroup("meeting.txt", 44L, Array.Empty<FileEntry>()),
        };
        var hashC1 = "821a7efb8d49dad09c77ff47e829a28db9a9642ed33b83fd55b640e364137a89";
        var hashX1 = "a231b86dbb971a220623d3b5bddf06548f74ac3efed175489f3b22c9a735ec20";
        var hashX2 = "1675a7b3e7abab3d2c3ebc80b226ff05955abf309fec605c7e9e7d5df91eb032";
        var identical = new[]
        {
            new IdenticalDuplicate(hashC1, 96L, new[]
            {
                new FileEntry { Path = Path.Combine(rootPath, "docs/backup/meeting-photo.jpg"), Size = 96, MtimeUtc = started, Attributes = FileAttributes.Normal, VolumeId = "v", FileId = "f1" },
                new FileEntry { Path = Path.Combine(rootPath, "docs/meeting-photo.jpg"), Size = 96, MtimeUtc = started, Attributes = FileAttributes.Normal, VolumeId = "v", FileId = "f2" },
                new FileEntry { Path = Path.Combine(rootPath, "photos/meeting-photo.jpg"), Size = 96, MtimeUtc = started, Attributes = FileAttributes.Normal, VolumeId = "v", FileId = "f3" },
            }),
        };
        var conflicts = new[]
        {
            new RealConflict("budget.xlsx", 262144L, new[]
            {
                new ConflictMember(Path.Combine(rootPath, "projects/budget-DESKTOP-ABC123 (conflicted copy).xlsx"), hashX2),
                new ConflictMember(Path.Combine(rootPath, "projects/budget.xlsx"), hashX1),
            }),
        };
        var placeholders = new List<PlaceholderRecord>
        {
            new() { Path = "dead file/old report.docx", Kinds = ["offline"], SizeBytes = 10485760 },
            new() { Path = "large files/video-lesson.mp4", Kinds = ["recall_on_data_access"], SizeBytes = 524288000 },
        };
        return (new ScanResult(groups, identical, conflicts), telemetry, placeholders, rootPath, started, finished);
    }

    private static void AssertRootOrder(string json)
    {
        var idx = json.IndexOf("\"report_schema_version\"");
        Assert.True(idx >= 0);
        Assert.True(json.IndexOf("\"algorithm\"") > idx);
        Assert.True(json.IndexOf("\"hash_version\"") > json.IndexOf("\"algorithm\""));
        Assert.True(json.IndexOf("\"normalization_rules_version\"") > json.IndexOf("\"hash_version\""));
        Assert.True(json.IndexOf("\"generated_from\"") > json.IndexOf("\"normalization_rules_version\""));
        var idxRf = json.IndexOf("\"generated_from\"");
        var idxRp = json.IndexOf("\"root_path\"", idxRf);
        var idxSsu = json.IndexOf("\"scan_started_utc\"", idxRp);
        var idxSfu = json.IndexOf("\"scan_finished_utc\"", idxSsu);
        Assert.True(idxRp > idxRf && idxSsu > idxRp && idxSfu > idxSsu);
        var idxTel = json.IndexOf("\"telemetry\"");
        Assert.True(idxTel > idxRf);
        AssertTelemetryOrder(json, idxTel);
        var idxGrps = json.IndexOf("\"groups\"");
        var idxIddups = json.IndexOf("\"identical_duplicates\"");
        var idxRc = json.IndexOf("\"real_conflicts\"");
        var idxPl = json.IndexOf("\"placeholders\"");
        Assert.True(idxGrps >= 0 && idxIddups > idxGrps && idxRc > idxIddups && idxPl > idxRc);
    }

    private static void AssertTelemetryOrder(string json, int startIndex)
    {
        var expected = new[] { "files_enumerated", "files_placeholder", "files_skipped", "files_partial_hashed", "files_full_hashed", "bytes_read", "bytes_read_partial", "bytes_read_full", "placeholder_bytes_read" };
        var within = json.Substring(startIndex);
        var end = within.IndexOf("\"groups\"");
        if (end > 0) within = within[..end];
        int last = -1;
        foreach (var key in expected)
        {
            var i = within.IndexOf($"\"{key}\"");
            Assert.True(i > last, $"telemetry: key '{key}' out of order");
            last = i;
        }
    }

    private static string MaskTimestamps(string json) =>
        json.Replace("2026-08-22T19:40:00.000Z", "MASKED")
            .Replace("2026-08-22T19:40:00.041Z", "MASKED");
}
