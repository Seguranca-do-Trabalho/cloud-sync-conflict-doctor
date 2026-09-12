namespace Doctor.Tests;

using System.Text.RegularExpressions;
using Doctor.Core;
using Xunit;

/// <summary>
/// T15 (t_5554ef78) — EPIC 08: quarantine + restore (SPEC §18, §22; ADR-0002;
/// ADR-0010; docs/contracts.md). Rules proven here:
/// 1. Move ⇒ original leaves, dated payload exists, manifest exists and carries ALL
///    fields from ADR-0010 §2, BLAKE3 hash verified against payload;
/// 2. Restore ⇒ original back byte-identical, hash preserved (§22);
/// 3. Restore to occupied destination ⇒ NEVER overwrites: fails with exception and
///    nothing is touched (occupant, payload and manifest remain byte-identical);
/// 4. Same names in different folders ⇒ collision-free payload, manifest sorted
///    by path in UTF-8 bytes (ADR-0003 rule 1);
/// 5. Same state + same timestamp ⇒ same operation_id and byte-identical
///    manifest (determinism ADR-0010 §1);
/// 6. Stale metadata between L0 and move ⇒ item skipped, file stays where it is,
///    honest record (ADR-0010 §3 — TOCTOU revalidation);
/// 7. Failure mid-move ⇒ honest partial manifest + exception; nothing moved
///    goes unrecorded (ADR-0002 item 4, ADR-0010 §3);
/// 8. Static guard: ZERO deletion APIs in Doctor.Core (§22; ADR-0002 item 5).
/// </summary>
public sealed class QuarantineTests : IDisposable
{
    private readonly string _root;

    public QuarantineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"t15-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup: OS temp reclaims later
        }
    }

    // ------------------------------------------------------------------
    // NDES-01 — complete move: quarantine + manifest + hash verified
    // ------------------------------------------------------------------
    [Fact]
    public void Move_OriginalLeaves_PayloadAndManifestExist_HashVerified()
    {
        var path = CreateFile("docs/report.txt", Content(0x51));
        var svc = NewService();

        var result = svc.Move(
            [new QuarantineItem(Entry(path), "IDENTICAL_DUPLICATE", "KEEP_NEWEST")],
            DefaultPlan());

        // original left; §18/ADR-0010 structure exists
        Assert.False(File.Exists(path));
        Assert.True(Directory.Exists(result.QuarantineDirectory));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.StartsWith(
            Path.Combine(_root, "ConflictDoctor", "quarantine") + Path.DirectorySeparatorChar,
            result.QuarantineDirectory);
        Assert.Equal("completed", result.Status);
        Assert.Single(result.MovedPaths);

        // manifest: EXACT fields from ADR-0010 §2
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        var root = json.RootElement;
        Assert.Equal(
            new[] { "manifest_version", "operation_id", "created_utc", "items", "status" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("manifest_version").GetInt32());
        Assert.Equal("completed", root.GetProperty("status").GetString());

        var item = root.GetProperty("items")[0];
        Assert.Equal(
            new[] { "original_path", "quarantine_path", "size", "mtime_utc", "hash",
                    "algorithm", "hash_version", "reason", "rule" },
            item.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(path, item.GetProperty("original_path").GetString());
        Assert.Equal(Content(0x51).LongLength, item.GetProperty("size").GetInt64());
        Assert.Equal("BLAKE3", item.GetProperty("algorithm").GetString());
        Assert.Equal(1, item.GetProperty("hash_version").GetInt32());
        Assert.Equal("IDENTICAL_DUPLICATE", item.GetProperty("reason").GetString());
        Assert.Equal("KEEP_NEWEST", item.GetProperty("rule").GetString());

        // manifest hash == BLAKE3 recalculated INDEPENDENTLY of payload
        var absolutePayload = Path.Combine(
            result.QuarantineDirectory,
            item.GetProperty("quarantine_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(absolutePayload));
        var independentHash = Convert.ToHexString(
            Blake3.Hasher.Hash(File.ReadAllBytes(absolutePayload)).AsSpan()).ToLowerInvariant();
        Assert.Equal(independentHash, item.GetProperty("hash").GetString());

        // operation_id in ADR-0010 §1 format: yyyyMMddTHHmmssZ-8hex
        Assert.Matches(@"^\d{8}T\d{6}Z-[0-9a-f]{8}$", root.GetProperty("operation_id").GetString());
    }

    // ------------------------------------------------------------------
    // NDES-02 — restore: original back byte-identical, hash preserved
    // ------------------------------------------------------------------
    [Fact]
    public void Restore_AfterMove_OriginalBackByteIdentical_HashPreserved()
    {
        var path = CreateFile("docs/report.txt", Content(0x77));
        var originalContent = File.ReadAllBytes(path);
        var svc = NewService();

        var move = svc.Move(
            [new QuarantineItem(Entry(path), "REAL_CONFLICT", "KEEP_LARGEST")],
            DefaultPlan());

        var restore = svc.Restore(move.OperationId, _root);

        Assert.True(File.Exists(path));
        Assert.Equal(originalContent, File.ReadAllBytes(path));

        // hash preserved: same content ⇒ same BLAKE3 recorded in manifest
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(move.ManifestPath));
        var hashInManifest = json.RootElement.GetProperty("items")[0].GetProperty("hash").GetString();
        var restoredHash = Convert.ToHexString(
            Blake3.Hasher.Hash(File.ReadAllBytes(path)).AsSpan()).ToLowerInvariant();
        Assert.Equal(restoredHash, hashInManifest);

        // history never deleted: manifest marks restoration
        Assert.Equal("restored", json.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(restore.OperationId, move.OperationId);
        Assert.Equal(path, restore.RestoredPaths.Single());
    }

    // ------------------------------------------------------------------
    // NDES-03 — restore to occupied destination: exception, NOTHING touched
    // ------------------------------------------------------------------
    [Fact]
    public void Restore_OccupiedDestination_FailsWithException_NothingTouched()
    {
        var path = CreateFile("docs/a.txt", Content(0x11));
        var svc = NewService();

        var move = svc.Move(
            [new QuarantineItem(Entry(path), "IDENTICAL_DUPLICATE", "KEEP_NEWEST")],
            DefaultPlan());

        // different occupier takes the original path
        var occupier = Content(0xDE);
        File.WriteAllBytes(path, occupier);

        var occupierBytes = File.ReadAllBytes(path);
        var payloadBefore = File.ReadAllBytes(SinglePayload(move));
        var manifestBefore = File.ReadAllBytes(move.ManifestPath);

        var exception = Assert.Throws<RestoreConflictException>(
            () => svc.Restore(move.OperationId, _root));

        Assert.Contains(path, exception.Message);

        // nothing was touched: occupier intact, payload intact, manifest intact
        Assert.Equal(occupierBytes, File.ReadAllBytes(path));
        Assert.NotEqual(Content(0x11), occupierBytes);
        Assert.Equal(payloadBefore, File.ReadAllBytes(SinglePayload(move)));
        Assert.Equal(manifestBefore, File.ReadAllBytes(move.ManifestPath));
    }

    // ------------------------------------------------------------------
    // same names: collision-free payload + manifest sorted by path
    // ------------------------------------------------------------------
    [Fact]
    public void Move_SameNamesInDifferentFolders_DistinctPayload_ManifestSorted()
    {
        var b = CreateFile("b/note.txt", Content(0x02));
        var a = CreateFile("a/note.txt", Content(0x01));
        var svc = NewService();

        var result = svc.Move(
            [
                new QuarantineItem(Entry(b), "IDENTICAL_DUPLICATE", "KEEP_NEWEST"),
                new QuarantineItem(Entry(a), "IDENTICAL_DUPLICATE", "KEEP_NEWEST"),
            ],
            DefaultPlan());

        Assert.Equal(2, result.MovedPaths.Count);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        var items = json.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        // canonical order by path in UTF-8 bytes, never call order
        var paths = items.EnumerateArray().Select(i => i.GetProperty("original_path").GetString()).ToArray();
        Assert.Equal(new[] { a, b }, paths);
        Assert.Equal(paths, paths.OrderBy(p => p, StringComparer.Ordinal).ToArray());

        // distinct opaque names in payload (ADR-0010 §1)
        var payloads = items.EnumerateArray().Select(i => i.GetProperty("quarantine_path").GetString()).ToArray();
        Assert.NotEqual(payloads[0], payloads[1]);
        Assert.All(payloads, p => Assert.Matches(@"^payload/\d{4}\.dat$", p!));
    }

    // ------------------------------------------------------------------
    // determinism: same state ⇒ same operation_id and identical manifest
    // ------------------------------------------------------------------
    [Fact]
    public void Move_SameStateTwice_SameOperationId_ManifestByteIdentical()
    {
        var path = CreateFile("docs/x.bin", Content(0xC3));
        var svc = NewService();
        var plan = DefaultPlan(); // frozen timestamp in plan

        var first = svc.Move([new QuarantineItem(Entry(path), "R", "RULE")], plan);
        var firstManifest = File.ReadAllBytes(first.ManifestPath);

        // return file to pre-operation state and clean quarantine:
        // second run starts from the SAME absolute state (same path)
        svc.Restore(first.OperationId, _root);
        Directory.Delete(Path.Combine(_root, "ConflictDoctor"), recursive: true);
        File.SetLastWriteTimeUtc(path, FixedEntryMtime);

        var second = svc.Move([new QuarantineItem(Entry(path), "R", "RULE")], plan);

        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(firstManifest, File.ReadAllBytes(second.ManifestPath));
    }

    // ------------------------------------------------------------------
    // TOCTOU (ADR-0010 §3): stale metadata ⇒ skip, don't move, record
    // ------------------------------------------------------------------
    [Fact]
    public void Move_StaleMetadata_ItemSkipped_FileStaysWhereItIs()
    {
        var path = CreateFile("docs/stale.txt", Content(0x33));
        var entry = Entry(path);

        // tree changes AFTER L0 snapshot: size diverges
        File.WriteAllBytes(path, Content(0x44));

        var svc = NewService();
        var result = svc.Move([new QuarantineItem(entry, "R", "RULE")], DefaultPlan());

        Assert.Empty(result.MovedPaths);
        Assert.Equal([path], result.SkippedStaleMetadata.ToArray());
        Assert.True(File.Exists(path)); // never silently disappeared

        // honest record: manifest exists, no item faking movement
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        Assert.Equal(0, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(
            [path],
            json.RootElement.GetProperty("skipped_stale_metadata")
                .EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    // ------------------------------------------------------------------
    // ADR-0002 item 4 / ADR-0010 §3: failure mid-move ⇒ honest partial + exception
    // ------------------------------------------------------------------
    [Fact]
    public void Move_FailureOnSecondItem_HonestPartialManifest_AndException()
    {
        // Canonical order by path (ADR-0003): "a.txt" < "b.txt" in UTF-8 bytes,
        // so "a.txt" moves FIRST even though passed second in the list.
        var a = CreateFile("a.txt", Content(0x01));
        var b = CreateFile("b.txt", Content(0x02));
        var svc = NewService(failMoveOn: 2); // 1st move ok, 2nd explodes

        var exception = Assert.ThrowsAny<Exception>(() => svc.Move(
            [
                new QuarantineItem(Entry(b), "R", "RULE"),
                new QuarantineItem(Entry(a), "R", "RULE"),
            ],
            DefaultPlan()));

        // first item in canonical order: moved AND recorded
        Assert.False(File.Exists(a));
        Assert.IsType<QuarantinePartialException>(exception);
        var partial = (QuarantinePartialException)exception;
        Assert.True(File.Exists(partial.PartialManifestPath));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(partial.PartialManifestPath));
        Assert.Equal("partial", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(a, json.RootElement.GetProperty("items")[0].GetProperty("original_path").GetString());

        // second item in order: untouched — nothing moved goes unrecorded
        Assert.True(File.Exists(b));
    }

    // ------------------------------------------------------------------
    // static guard (§22; ADR-0002 item 5; NDES-05): zero deletes in module
    // ------------------------------------------------------------------
    [Fact]
    public void QuarantineModule_ZeroDeletionCalls_InDoctorCore()
    {
        var coreRoot = SourceRoot("Doctor.Core");
        var violations = new List<string>();

        foreach (var cs in Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (cs.EndsWith("obj") || cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(cs))
            {
                lineNumber++;
                foreach (var pattern in ForbiddenPatterns)
                {
                    if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                    {
                        violations.Add(
                            $"{Path.GetRelativePath(coreRoot, cs)}:{lineNumber} [{pattern}] {line.Trim()}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Anti-delete guard violated in Doctor.Core — only permitted touch on user " +
            $"content is File.Move to quarantine (ADR-0002):\n{string.Join("\n", violations)}");
    }

    /// <summary>Mutation validation: detector recognizes each forbidden API.</summary>
    [Theory]
    [InlineData("File.Delete(path);")]
    [InlineData("Directory.Delete(folder, recursive: true);")]
    [InlineData("[DllImport(\"kernel32.dll\")] static extern bool DeleteFileW(string p);")]
    [InlineData("SetFileInformationByHandle(h, FileDispositionInfo, &info, 4);")]
    [InlineData("var info = new FILE_DISPOSITION_INFO();")]
    public void AntiDeleteDetector_RecoignizesForbiddenApi_InContaminatedSnippet(string snippet)
    {
        Assert.True(ForbiddenPatterns.Any(p => Regex.IsMatch(snippet, p, RegexOptions.IgnoreCase)),
            $"Detector did not recognize snippet: {snippet}");
    }

    // ==================================================================
    // test infrastructure
    // ==================================================================

    /// <summary>Permanent deletion patterns forbidden in ALL of Doctor.Core.
    /// File.Move is the ONLY sanctioned destructive-permissive API (ADR-0010 §3).</summary>
    private static readonly string[] ForbiddenPatterns =
    [
        @"\bFile\.Delete\s*\(",
        @"\bDirectory\.Delete\s*\(",
        @"\bFileSystem\.DeleteFile\s*\(",
        @"\bFileSystem\.DeleteDirectory\s*\(",
        @"\.Delete\s*\(\s*\)",
        @"\bDeleteFileW?\b",
        @"\bRemoveDirectoryW?\b",
        @"\bFILE_DISPOSITION_INFO\b",
        @"\bFileDispositionInfo\b",
    ];

    private static readonly DateTimeOffset FrozenTimestamp =
        new(2026, 8, 22, 19, 45, 0, TimeSpan.Zero);

    private static readonly DateTime FixedEntryMtime =
        new(2026, 8, 20, 10, 30, 0, DateTimeKind.Utc);

    private QuarantineService NewService(int? failMoveOn = null)
    {
        var counter = 0;
        return new QuarantineService(
            moveOverride: failMoveOn is null
                ? null
                : (source, destination) =>
                {
                    counter++;
                    if (counter >= failMoveOn.Value)
                    {
                        throw new IOException($"simulated failure on move #{counter}");
                    }

                    File.Move(source, destination);
                });
    }

    private QuarantinePlan DefaultPlan() => new(_root, FrozenTimestamp);

    private string SinglePayload(QuarantineOperationResult move)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(move.ManifestPath));
        var relative = json.RootElement.GetProperty("items")[0]
            .GetProperty("quarantine_path").GetString()!;
        return Path.Combine(move.QuarantineDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private string SourceRoot(string project)
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CloudSyncConflictDoctor.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", project);
    }

    private static byte[] Content(byte seed) =>
        Enumerable.Range(0, 4096).Select(i => (byte)(seed + (i % 97))).ToArray();

    private string CreateFile(string relativePath, byte[] content)
    {
        var absolute = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, content);
        File.SetLastWriteTimeUtc(absolute, FixedEntryMtime);
        return absolute;
    }

    private FileEntry Entry(string path)
    {
        var info = new FileInfo(path);
        return new FileEntry
        {
            Path = path,
            Size = info.Length,
            MtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc),
            Attributes = FileAttributes.Normal,
            VolumeId = "t15-volume",
            FileId = path,
        };
    }
}
