namespace Doctor.Tests;

using Doctor.Core;
using Xunit;

/// <summary>
/// SEG-04 and SEG-05 (T-02 from threat-model, rule R3): defenses against junction/symlink loops
/// and against external content leakage through Level 0 enumeration.
///
/// SEG-04 — <see cref="Security_JunctionLoop_TerminatesWithoutDescent"/>: cycle a→b→a
/// via directory symlinks terminates in finite time; the symlinks enter as leaves
/// (registered in Errors with IsReparsePoint=true), never as descent points.
///
/// SEG-05 — <see cref="Security_ReparseDir_PointingOutsideRoot_NotEntered"/>: directory
/// symlink pointing outside the scanned root never exposes external content in the
/// report or in the file list; the symlink entry is a leaf registered in Errors.
/// </summary>
public sealed class SecurityJunctionSymlinkTests : IDisposable
{
    private readonly string _root;
    private readonly string _outsideRoot;

    public SecurityJunctionSymlinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"seg04-05-{Guid.NewGuid():N}");
        _outsideRoot = Path.Combine(Path.GetTempPath(), $"seg04-05-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideRoot);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _outsideRoot })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    // =====================================================================
    // SEG-04 — Security_JunctionLoop_TerminatesWithoutDescent (T-02, P1)
    // =====================================================================
    [Fact]
    [Trait("Category", "Security")]
    public void Security_JunctionLoop_TerminatesWithoutDescent()
    {
        // Build real cycle a->b->a with directory symlinks (simulates junction on Linux).
        var dirA = Path.Combine(_root, "a");
        var dirB = Path.Combine(_root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        File.WriteAllText(Path.Combine(dirA, "x.txt"), "content-from-a");
        File.WriteAllText(Path.Combine(dirB, "y.txt"), "content-from-b");

        // Directory symlinks in cycle: a/link -> b, b/link -> a.
        var linkAB = Path.Combine(dirA, "link");
        var linkBA = Path.Combine(dirB, "link");
        File.CreateSymbolicLink(linkAB, dirB);
        File.CreateSymbolicLink(linkBA, dirA);

        // Execute complete Level 0 enumeration (production pipeline).
        var enumerator = new OrderedFileEnumerator(new CrossPlatformEnumerator());
        var result = enumerator.Enumerate(_root, CancellationToken.None);

        // 1. Terminates in finite time: reaching here = scan did not loop.
        // 2. Real files present and correct.
        var paths = result.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Contains(paths, p => p.EndsWith("x.txt", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("y.txt", StringComparison.Ordinal));

        // 3. Directory symlinks enter as LEAVES registered in Errors, never as
        //    entries in the Files list (no descent).
        Assert.DoesNotContain(result.Files, f => f.Path.Contains("link", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Path.EndsWith(Path.Combine("a", "link"), StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Path.EndsWith(Path.Combine("b", "link"), StringComparison.Ordinal));

        // 4. Consistent telemetry: nothing was read (Level 0 does not read content).
        Assert.Equal(0, result.Telemetry.BytesRead);
        Assert.Equal(0, result.Telemetry.PlaceholderBytesRead);
        Assert.Equal(result.Files.Count, result.Telemetry.FilesEnumerated);
    }

    // =====================================================================
    // SEG-05 — Security_ReparseDir_PointingOutsideRoot_NotEntered (T-02, P1)
    // =====================================================================
    [Fact]
    [Trait("Category", "Security")]
    public void Security_ReparseDir_PointingOutsideRoot_NotEntered()
    {
        // Create REAL file inside root (to prove the scan can see internal content).
        var internalFile = Path.Combine(_root, "internal.txt");
        File.WriteAllText(internalFile, "i-am-internal");

        // Create file OUTSIDE root (privacy honeypot).
        var externalFile = Path.Combine(_outsideRoot, "secret.txt");
        File.WriteAllText(externalFile, "SHOULD-NOT-APPEAR-IN-REPORT");

        // Directory symlink inside root pointing OUTSIDE.
        var externalLink = Path.Combine(_root, "external-link");
        File.CreateSymbolicLink(externalLink, _outsideRoot);

        // Execute Level 0 enumeration.
        var enumerator = new OrderedFileEnumerator(new CrossPlatformEnumerator());
        var result = enumerator.Enumerate(_root, CancellationToken.None);

        // 1. Internal file present.
        Assert.Contains(result.Files, f => f.Path.Equals(internalFile, StringComparison.Ordinal));

        // 2. External content NEVER appears in report (neither in Files nor in Errors).
        Assert.DoesNotContain(result.Files, f => f.Path.Contains(_outsideRoot, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, e => e.Path.Contains(_outsideRoot, StringComparison.Ordinal));

        // 3. Directory symlink enters as a registered leaf (reparse error), not as
        //    a descent point.
        Assert.Contains(result.Errors, e => e.Path.Equals(externalLink, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files, f => f.Path.Equals(externalLink, StringComparison.Ordinal));

        // 4. External file remains untouched (proof that scan did not access target).
        Assert.Equal("SHOULD-NOT-APPEAR-IN-REPORT", File.ReadAllText(externalFile));

        // 5. Clean telemetry.
        Assert.Equal(0, result.Telemetry.PlaceholderBytesRead);
    }
}
