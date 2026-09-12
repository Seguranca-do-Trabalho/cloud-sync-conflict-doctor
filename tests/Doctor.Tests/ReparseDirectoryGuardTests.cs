namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — PLH-04 / threat-model T-02: enumeration NEVER traverses directory
/// reparse points (junction/symlink/mount point) — rule 4 of ADR-0004.
/// Real tree in tmp with symlink cycle: the scan terminates in finite time,
/// does not descend through the cycle and records the reparse as a leaf (ScanError) and continues.
/// </summary>
public class ReparseDirectoryGuardTests : IDisposable
{
    private readonly string _root;

    public ReparseDirectoryGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdt09-loop-" + Guid.NewGuid().ToString("N"));
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

    private FileAttributes SymlinkAttrs(string linkPath) =>
        File.GetAttributes(Path.Combine(_root, linkPath).Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void SymlinkCycle_TerminatesWithoutTraversing_ContinuesScan()
    {
        // Tree: normal.txt before cycle, cycle a->b->a, normal.txt after.
        // If enumeration traversed the cycle, it would not terminate in finite time.
        _ = Directory.CreateDirectory(Path.Combine(_root, "a"));
        _ = Directory.CreateDirectory(Path.Combine(_root, "b"));
        File.WriteAllText(Path.Combine(_root, "before.txt"), "before");
        File.WriteAllText(Path.Combine(_root, "a", "x.txt"), "xxx");
        File.WriteAllText(Path.Combine(_root, "b", "y.txt"), "yyy");
        File.WriteAllText(Path.Combine(_root, "after.txt"), "after");
        File.CreateSymbolicLink(Path.Combine(_root, "a", "loop"), _root + "/b");
        File.CreateSymbolicLink(Path.Combine(_root, "b", "loop"), _root + "/a");

        var result = new CrossPlatformEnumerator().Enumerate(_root, CancellationToken.None);

        var paths = result.Files.Select(f => f.Path).ToArray();
        Assert.Contains(paths, p => p.EndsWith("before.txt", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("after.txt", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("x.txt", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("y.txt", StringComparison.Ordinal));

        // Exactly the 4 real files: if cycle were traversed, x/y would
        // appear duplicated on each pass (and scan would not even terminate).
        Assert.Equal(4, paths.Count(p => p.EndsWith(".txt", StringComparison.Ordinal)));

        // Directory reparse registered as leaf (placeholders[]/errors) — never silent.
        Assert.Contains(result.Errors, e => e.Path.EndsWith(Path.Combine("a", "loop"), StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Path.EndsWith(Path.Combine("b", "loop"), StringComparison.Ordinal));

        // Zero bytes read: enumeration does not read content; consistent telemetry.
        Assert.Equal(0, result.Telemetry.PlaceholderBytesRead);
        Assert.Equal(result.Files.Count, result.Telemetry.FilesEnumerated);
    }

    [Fact]
    public void DirectorySymlink_OutsideRoot_IsNotTraversed()
    {
        // Threat-model T-02: directory symlink pointing outside root.
        // External content NEVER appears in report (local-first privacy).
        var outside = Path.Combine(Path.GetTempPath(), "cdt09-outside-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside root");
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(_root, "sub"));
            File.CreateSymbolicLink(Path.Combine(_root, "sub", "escape"), outside);

            var result = new CrossPlatformEnumerator().Enumerate(_root, CancellationToken.None);

            Assert.DoesNotContain(result.Files, f => f.Path.Contains("secret", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.Path.EndsWith("escape", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void FileSymlink_EntersMarkedAsPlaceholder_NeverTraversed()
    {
        // SPEC §6: reparse points are not followed. On POSIX, FILE symlink is the
        // analog: enters the Level 0 list marked, without opening the target.
        File.WriteAllText(Path.Combine(_root, "real.txt"), "real content");
        File.CreateSymbolicLink(Path.Combine(_root, "shortcut.txt"), Path.Combine(_root, "real.txt"));

        var result = new CrossPlatformEnumerator().Enumerate(_root, CancellationToken.None);

        var shortcut = Assert.Single(result.Files, f => f.Path.EndsWith("shortcut.txt", StringComparison.Ordinal));
        Assert.True(shortcut.IsPlaceholder);
        Assert.Equal(PlaceholderKind.ReparsePoint, shortcut.PlaceholderKind);
        Assert.Contains(result.Files, f => f.Path.EndsWith("real.txt", StringComparison.Ordinal));
    }
}
