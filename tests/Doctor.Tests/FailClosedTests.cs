using Doctor.Core;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// S11-5 — Fail-closed (T-11): without verified full hash, no decision.
/// Read/hash failure in L3 refuses classification of ENTIRE GROUP:
/// nothing becomes duplicate or conflict; group emerges as auditable
/// UnresolvedGroup and is never eligible for resolution/quarantine.
/// </summary>
public sealed class FailClosedTests
{
    private static FileEntry Entry(string path, long size, string fileId) => new()
    {
        Path = path,
        Size = size,
        MtimeUtc = DateTimeOffset.UnixEpoch,
        Attributes = FileAttributes.Normal,
        VolumeId = "vol-fc",
        FileId = fileId,
        IsPlaceholder = false,
        PlaceholderKind = null,
    };

    /// <summary>Hasher that fails reading on specified paths (simulates IO error).</summary>
    private sealed class FlakyHasher(params string[] failingPaths) : IHasher
    {
        public string PartialHash(FileEntry entry, CancellationToken ct) =>
            Failing(entry) ? throw new IOException("simulated: disk vanished") : "p-" + entry.Path;

        public string FullHash(FileEntry entry, CancellationToken ct) =>
            Failing(entry) ? throw new IOException("simulated: disk vanished") : "f-" + entry.Path;

        private bool Failing(FileEntry entry) => failingPaths.Contains(entry.Path);
    }

    [Fact]
    public void Fct01_FailingHashOnOneMember_EntireGroupUnresolved_NeverDecided()
    {
        var root = CreateTree(("a/Rel.bin", "AAA"), ("b/Rel.bin", "AAA"));
        try
        {
            var pipeline = new ScanPipeline(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()),
                new FlakyHasher(root + "/b/Rel.bin"),
                streams: null);

            var result = pipeline.Run(root);

            Assert.Empty(result.IdenticalDuplicates);
            Assert.Empty(result.RealConflicts);

            var group = Assert.Single(result.UnresolvedGroups);
            var member = Assert.Single(group.Members);
            Assert.Equal("/b/Rel.bin", member.Path.EndsWith("/b/Rel.bin") ? "/b/Rel.bin" : member.Path);
            Assert.Contains("simulated", member.Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Fct02_HealthyScan_UnresolvedEmpty()
    {
        var root = CreateTree(("a/X.txt", "one"), ("b/X.txt", "one"), ("single.txt", "two"));
        try
        {
            var pipeline = new ScanPipeline(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()),
                new Blake3Hasher(),
                streams: null);

            var result = pipeline.Run(root);

            Assert.Empty(result.UnresolvedGroups);
            Assert.Single(result.IdenticalDuplicates);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Fct03_AllMembersFailing_UnresolvedGroupWithAllReasons()
    {
        var root = CreateTree(("a/Y.bin", "CCC"), ("b/Y.bin", "CCC"));
        try
        {
            var pipeline = new ScanPipeline(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()),
                new FlakyHasher(root + "/a/Y.bin", root + "/b/Y.bin"),
                streams: null);

            var result = pipeline.Run(root);

            Assert.Empty(result.IdenticalDuplicates);
            Assert.Empty(result.RealConflicts);

            var group = Assert.Single(result.UnresolvedGroups);
            Assert.Equal(2, group.Members.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Fct04_InaccessibleContentOnOpen_UnresolvedGroup_NoExceptionToCaller()
    {
        // TOCTOU T-04/T-05: content inaccessible at time of full hash
        // (file truncated/locked/removed between L2 and L3) → fail-closed:
        // unresolved group, scan does NOT abort and NEVER classifies without data.
        var root = CreateTree(("a/Z.txt", "DELTA"), ("b/Z.txt", "DELTA"));
        try
        {
            var streamSource = new LockedFileStreamSource(root + "/b/Z.txt");
            var pipeline = new ScanPipeline(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()),
                new Blake3Hasher(),
                streamSource);

            var result = pipeline.Run(root); // does not throw

            Assert.Empty(result.IdenticalDuplicates);
            Assert.Empty(result.RealConflicts);

            var group = Assert.Single(result.UnresolvedGroups);
            Assert.Single(group.Members);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Source refusing open of target path (simulates lock/race condition T-04).</summary>
    private sealed class LockedFileStreamSource(string lockedPath) : IStreamSource
    {
        public Stream OpenRead(FileEntry entry)
        {
            if (entry.Path.EndsWith(lockedPath) || lockedPath.EndsWith(entry.Path))
            {
                throw new IOException("simulated: file locked by another process");
            }

            return File.OpenRead(entry.Path);
        }
    }

    private static string CreateTree(params (string Rel, string Content)[] files)
    {
        var root = Directory.CreateTempSubdirectory("cd-s115").FullName;
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }
}
