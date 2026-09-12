namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T18 (t_c894ff83) — S11-2: reparse policy in ordered enumerator (SPEC §6,
/// threat-model T-02, ADR-0004 rule 4). Defense in depth ABOVE physical
/// enumerator: <see cref="OrderedFileEnumerator"/> applies <see cref="ReparsePolicy"/> —
/// (1) marks every entry with <see cref="FileEntry.IsReparsePoint"/>;
/// (2) rejects paths that return to the same inode (visited guard by
///     (VolumeId, FileId) — mandatory mitigation for T-02);
/// (3) rejects entries beyond max depth ceiling (<see cref="ReparsePolicy.MaxDepth"/> = 16),
///     recording each rejection in <see cref="EnumerationResult.Errors"/> — no silent failure
///     (contracts.md R10).
/// </summary>
public class ReparsePolicyTests : IDisposable
{
    private readonly string _root;

    public ReparsePolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdt18-reparse-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Simulated physical enumerator: yields entries in arbitrary order and counts content reads.</summary>
    private sealed class FakePhysicalEnumerator : IFileEnumerator
    {
        private readonly FileEntry[] _physical;
        public FakePhysicalEnumerator(params FileEntry[] physical) => _physical = physical;
        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
            => new(_physical.ToArray(), Array.Empty<ScanError>(), new ScanTelemetry());

        /// <summary>Represents content reading — enumeration must NEVER call this.</summary>
        public int ReadBytesCallCount { get; private set; }
        public byte[] ReadBytes(FileEntry entry) { ReadBytesCallCount++; return Array.Empty<byte>(); }
    }

    private static FileEntry Entry(
        string path,
        long size = 1,
        FileAttributes attrs = FileAttributes.Normal,
        string volumeId = "vol-1",
        string? fileId = null)
        => new()
        {
            Path = path,
            Size = size,
            MtimeUtc = DateTimeOffset.UnixEpoch,
            Attributes = attrs,
            VolumeId = volumeId,
            FileId = fileId ?? "id-" + path,
        };

    // ------------------------------------------------------------------
    // 8 REPARSE test cases
    // ------------------------------------------------------------------

    [Fact]
    public void Reparse01_FileWithReparseBit_MarkedIsReparsePoint()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/link.dat", attrs: FileAttributes.ReparsePoint));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        var entry = Assert.Single(result.Files);
        Assert.True(entry.IsReparsePoint);
        Assert.True(entry.IsPlaceholder);
        Assert.Equal(PlaceholderKind.ReparsePoint, entry.PlaceholderKind);
    }

    [Fact]
    public void Reparse02_NormalFile_NotMarkedReparse()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/normal.txt"),
            Entry("/root/offline.docx", attrs: FileAttributes.Offline));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        Assert.All(result.Files, e => Assert.False(e.IsReparsePoint));
        var offline = result.Files.Single(f => f.Path.EndsWith("offline.docx", StringComparison.Ordinal));
        Assert.True(offline.IsPlaceholder);   // offline remains placeholder...
        Assert.False(offline.IsReparsePoint); // ...but is NOT reparse
    }

    [Fact]
    public void Reparse03_OriginMark_NeverClearedByProjection()
    {
        // Entry that ALREADY arrives marked IsReparsePoint=true from physical enumerator
        // (origin authority, same rule as PlaceholderPolicy fail-closed).
        var original = Entry("/root/link.txt") with { IsReparsePoint = true };
        var fake = new FakePhysicalEnumerator(original);

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        Assert.True(Assert.Single(result.Files).IsReparsePoint);
    }

    [Fact]
    public void Reparse04_FileSymlinkIntegration_IncludedMarkedInOrderedList()
    {
        File.WriteAllText(Path.Combine(_root, "real.txt"), "content");
        File.CreateSymbolicLink(Path.Combine(_root, "shortcut.txt"), Path.Combine(_root, "real.txt"));

        var result = new OrderedFileEnumerator(new CrossPlatformEnumerator())
            .Enumerate(_root, CancellationToken.None);

        var shortcut = Assert.Single(result.Files, f => f.Path.EndsWith("shortcut.txt", StringComparison.Ordinal));
        Assert.True(shortcut.IsReparsePoint);
        Assert.Equal(PlaceholderKind.ReparsePoint, shortcut.PlaceholderKind);
    }

    [Fact]
    public void Reparse05_DirectoryJunctionIntegration_IsLeafAndScanContinues()
    {
        _ = Directory.CreateDirectory(Path.Combine(_root, "target"));
        File.WriteAllText(Path.Combine(_root, "target", "inside.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "before.txt"), "a");
        File.CreateSymbolicLink(Path.Combine(_root, "junction"), Path.Combine(_root, "target"));

        var result = new OrderedFileEnumerator(new CrossPlatformEnumerator())
            .Enumerate(_root, CancellationToken.None);

        Assert.Contains(result.Files, f => f.Path.EndsWith("before.txt", StringComparison.Ordinal));
        Assert.Contains(result.Files, f => f.Path.EndsWith("inside.txt", StringComparison.Ordinal));
        // junction is a recorded leaf — never silent, never traversed as directory.
        Assert.Contains(result.Errors, e => e.Path.EndsWith("junction", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files, f => f.Path.Contains("junction", StringComparison.Ordinal));
    }

    [Fact]
    public void Reparse06_SymlinkOutsideRoot_ExternalContentDoesNotLeak()
    {
        var outside = Path.Combine(Path.GetTempPath(), "cdt18-outside-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(_root, "sub"));
            File.CreateSymbolicLink(Path.Combine(_root, "sub", "escape"), outside);

            var result = new OrderedFileEnumerator(new CrossPlatformEnumerator())
                .Enumerate(_root, CancellationToken.None);

            Assert.DoesNotContain(result.Files, f => f.Path.Contains("secret", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.Path.EndsWith("escape", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
    [Fact]
    public void Reparse07_CoherentTelemetry_WithReparseEntries()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/b.dat", attrs: FileAttributes.ReparsePoint),
            Entry("/root/a.txt"),
            Entry("/root/c.link", attrs: FileAttributes.ReparsePoint));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        Assert.Equal(3, result.Telemetry.FilesEnumerated);
        Assert.Equal(2, result.Telemetry.FilesPlaceholder);
        Assert.Equal(result.Files.Count, result.Telemetry.FilesEnumerated);
        Assert.Equal(0, result.Telemetry.PlaceholderBytesRead);
    }

    [Fact]
    public void Reparse08_ReparseNeverHasContentRead()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/link.large", size: 999, attrs: FileAttributes.ReparsePoint),
            Entry("/root/normal.txt"));

        _ = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        // Core proof: Level 0 enumeration NEVER reads content — not even for reparse.
        Assert.Equal(0, fake.ReadBytesCallCount);
    }

    // ------------------------------------------------------------------
    // 6 test cases for LOOP DETECTION / DEPTH CEILING
    // ------------------------------------------------------------------

    [Fact]
    public void Loop01_SameInodeDifferentPaths_DuplicateRejectedWithError()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/original.txt", fileId: "inode-777"),
            Entry("/root/loop/copy.txt", fileId: "inode-777")); // returns to same inode

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        // Retains FIRST occurrence in canonical order ("/root/loop/..." < "/root/original..."):
        // deterministic decision regardless of physical arrival order.
        var paths = result.Files.Select(f => f.Path).ToArray();
        Assert.Equal(new[] { "/root/loop/copy.txt" }, paths);
        var error = Assert.Single(result.Errors);
        Assert.Equal("/root/original.txt", error.Path);
        Assert.Contains("already-visited inode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loop02_TripleCycle_OnlyOriginalsRetained_DeterministicRejection()
    {
        // a -> b -> a: three paths, two inodes. The duplicate (regardless of physical
        // arrival order) is always the one with the LARGER canonical path.
        FileEntry[] physical =
        [
            Entry("/root/b/echo.txt", fileId: "i-2"),
            Entry("/root/a/target.txt", fileId: "i-1"),
            Entry("/root/a/loop/b/echo.txt", fileId: "i-2"),
        ];

        var r1 = new OrderedFileEnumerator(new FakePhysicalEnumerator(physical)).Enumerate("/root", CancellationToken.None);
        var r2 = new OrderedFileEnumerator(new FakePhysicalEnumerator(physical.Reverse().ToArray())).Enumerate("/root", CancellationToken.None);

        Assert.Equal(r1.Files, r2.Files);
        Assert.Equal(r1.Errors, r2.Errors);
        Assert.Equal(
            new[] { "/root/a/loop/b/echo.txt", "/root/a/target.txt" },
            r1.Files.Select(f => f.Path).ToArray());
        Assert.Equal("/root/b/echo.txt", Assert.Single(r1.Errors).Path);
    }

    [Fact]
    public void Loop03_DepthAbove16Ceiling_Rejected_Exact16Retained()
    {
        const string seg = "d";
        var inside = "/root/" + string.Join('/', Enumerable.Repeat(seg, 15)) + "/limit16.txt";   // depth 16
        var outside = "/root/" + string.Join('/', Enumerable.Repeat(seg, 16)) + "/overflow17.txt";   // depth 17

        var fake = new FakePhysicalEnumerator(Entry(inside), Entry(outside));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        Assert.Equal(new[] { inside }, result.Files.Select(f => f.Path).ToArray());
        var error = Assert.Single(result.Errors);
        Assert.Equal(outside, error.Path);
        Assert.Contains("depth", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loop04_RealCycleTreeIntegration_TerminatesInFiniteTimeWithoutDuplicates()
    {
        // Real cycle a->b->a via directory symlinks, passed through full ordered pipeline:
        // terminates, no duplicated files and with leaves recorded.
        _ = Directory.CreateDirectory(Path.Combine(_root, "a"));
        _ = Directory.CreateDirectory(Path.Combine(_root, "b"));
        File.WriteAllText(Path.Combine(_root, "a", "x.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "b", "y.txt"), "y");
        File.CreateSymbolicLink(Path.Combine(_root, "a", "loop"), Path.Combine(_root, "b"));
        File.CreateSymbolicLink(Path.Combine(_root, "b", "loop"), Path.Combine(_root, "a"));

        var result = new OrderedFileEnumerator(new CrossPlatformEnumerator())
            .Enumerate(_root, CancellationToken.None);

        var txtNames = result.Files
            .Where(f => f.Path.EndsWith(".txt", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.Path))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "x.txt", "y.txt" }, txtNames);
        Assert.Equal(result.Files.Count, result.Telemetry.FilesEnumerated);
        Assert.Contains(result.Errors, e => e.Path.EndsWith(Path.Combine("a", "loop"), StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Path.EndsWith(Path.Combine("b", "loop"), StringComparison.Ordinal));
    }

    [Fact]
    public void Loop05_Depth_PathOutsideRoot_NeverExceedsCeiling()
    {
        // Simulates entry whose logical root differs from supplied root: depth code
        // must return 0 instead of artificially inflating — otherwise rejection
        // by depth would harm paths outside root (security: false-positive loop).
        var fake = new FakePhysicalEnumerator(
            Entry("/root/real.txt"),
            Entry("/other/volume/jump.txt"));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        // No depth error — path outside root is not rejected.
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("depth", StringComparison.Ordinal));
        Assert.Equal(
            new[] { "/other/volume/jump.txt", "/root/real.txt" },
            result.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Loop06_Depth_RootWithTrailingSlash_CorrectDepth()
    {
        // Root passed as "/root/" (with trailing slash) and files inside it: depth must
        // be measurable even after TrimStart on relative.
        var fake = new FakePhysicalEnumerator(
            Entry("/root/d1/d2/final.txt"));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root/", CancellationToken.None);

        // Depth = 3 ("/d1/d2/final.txt") <= ceiling (16) — retained.
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("depth", StringComparison.Ordinal));
        Assert.Single(result.Files, f => f.Path.EndsWith("final.txt", StringComparison.Ordinal));
    }
}
