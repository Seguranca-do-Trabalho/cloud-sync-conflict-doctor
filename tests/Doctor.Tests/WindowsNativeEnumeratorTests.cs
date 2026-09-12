using System.Runtime.InteropServices;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_f706caaa) — WindowsNativeEnumerator: equivalence tests, FileId/VolumeId
/// stability, placeholder detection and reparse point handling.
///
/// All tests are marked with Trait("OS", "Windows") to run only on Windows CI runners.
/// On Linux they will be skipped.
/// </summary>
[Trait("OS", "Windows")]
public class WindowsNativeEnumeratorTests : IDisposable
{
    private readonly string _root;

    public WindowsNativeEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "t23-" + Guid.NewGuid().ToString("N"));
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
        }
    }

    [Fact]
    public void Enumerate_EquivalenceWithCrossPlatform_SamePathsInOrdinalOrder()
    {
#if !WINDOWS
        // Test marked with Trait("OS", "Windows") - does not compile on Linux.
        return;
#endif

        CreateTreeFixture();

        var enumeratorWindows = new OrderedFileEnumerator(new WindowsNativeEnumerator());
        var enumeratorXplat = new OrderedFileEnumerator(new CrossPlatformEnumerator());

        var windowsResult = enumeratorWindows.Enumerate(_root, CancellationToken.None);
        var xplatResult = enumeratorXplat.Enumerate(_root, CancellationToken.None);

        Assert.Equal(xplatResult.Files.Count, windowsResult.Files.Count);

        var windowsPaths = windowsResult.Files.Select(f => f.Path).ToArray();
        var xplatPaths = xplatResult.Files.Select(f => f.Path).ToArray();

        Assert.Equal(xplatPaths.OrderBy(p => p, StringComparer.Ordinal),
                     windowsPaths.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Enumerate_FileIdNotEmpty_StableBetweenScans()
    {
#if !WINDOWS
        // Test marked with Trait("OS", "Windows") - does not compile on Linux.
        return;
#endif

        CreateTreeFixture();

        var enumerator = new WindowsNativeEnumerator();

        var first = enumerator.Enumerate(_root, CancellationToken.None);
        var second = enumerator.Enumerate(_root, CancellationToken.None);

        Assert.True(first.Files.Count > 0, "There must be files in the tree");

        Assert.All(first.Files, f =>
        {
            Assert.False(string.IsNullOrEmpty(f.FileId), $"FileId empty for {f.Path}");
            Assert.False(string.IsNullOrEmpty(f.VolumeId), $"VolumeId empty for {f.Path}");
        });

        for (var i = 0; i < first.Files.Count; i++)
        {
            Assert.Equal(first.Files[i].FileId, second.Files[i].FileId);
            Assert.Equal(first.Files[i].VolumeId, second.Files[i].VolumeId);
        }
    }

    [Fact]
    public void Enumerate_PlaceholderMarked_WithoutOpeningContent()
    {
#if WINDOWS
        var filePath = Path.Combine(_root, "offline.txt");
        File.WriteAllText(filePath, "offline content");

        var winPath = filePath.Replace('/', '\\');
        NativeMethods.SetFileAttributesW(winPath, (uint)FileAttributes.Offline);

        try
        {
            var result = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

            var entry = Assert.Single(result.Files);
            Assert.True(entry.IsPlaceholder);
            Assert.Equal(PlaceholderKind.Offline, entry.PlaceholderKind);
        }
        finally
        {
            NativeMethods.SetFileAttributesW(winPath, (uint)FileAttributes.Normal);
        }
#else
        // Test marked with Trait("OS", "Windows") - does not compile on Linux.
        return;
#endif
    }

    [Fact]
    public void Enumerate_ReparseDirectory_LeafRegisteredAndMarked()
    {
#if WINDOWS
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "x");

        var junction = Path.Combine(_root, "junction");
        NativeMethods.CreateSymbolicLinkW(junction, target, true);

        try
        {
            var result = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

            var error = Assert.Single(result.Errors, e => e.Path.Contains("junction", StringComparison.Ordinal));
            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);

            // What needs to be guaranteed is that NOTHING is reached THROUGH the
            // junction. The previous version required that "inside.txt" not appear
            // anywhere — incorrect assertion, because the junction target is
            // INSIDE the scanned root (_root/target/inside.txt) and should indeed be
            // enumerated via the direct path. The test never ran (the body was
            // under `#if !WINDOWS return;` with the WINDOWS symbol never defined),
            // so the mistake went unnoticed.
            Assert.DoesNotContain(
                result.Files,
                f => f.Path.Contains("junction", StringComparison.Ordinal));

            // ...and the legitimate target continues to be enumerated via the real path.
            Assert.Contains(
                result.Files,
                f => f.Path.EndsWith(Path.Combine("target", "inside.txt"), StringComparison.Ordinal));
        }
        finally
        {
            NativeMethods.DeleteFileW(junction);
        }
#else
        // Test marked with Trait("OS", "Windows") - does not compile on Linux.
        return;
#endif
    }

    [Fact]
    public void Enumerate_FileSymlink_MarkedReparsePoint()
    {
#if WINDOWS
        var target = Path.Combine(_root, "real.txt");
        File.WriteAllText(target, "content");

        var link = Path.Combine(_root, "shortcut.txt");
        NativeMethods.CreateSymbolicLinkW(link, target, false);

        try
        {
            var result = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

            var entry = Assert.Single(result.Files, f => f.Path.Contains("shortcut.txt", StringComparison.Ordinal));
            Assert.True(entry.IsReparsePoint);
            Assert.True(entry.IsPlaceholder);
            Assert.Equal(PlaceholderKind.ReparsePoint, entry.PlaceholderKind);
        }
        finally
        {
            NativeMethods.DeleteFileW(link);
        }
#else
        // Test marked with Trait("OS", "Windows") - does not compile on Linux.
        return;
#endif
    }

    [Fact]
    public void Enumerate_TelemetryConsistent()
    {
#if !WINDOWS
        // Test marked with Trait("OS", "Windows") - does not compile on Linux.
        return;
#endif

        CreateTreeFixture();

        var result = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

        Assert.Equal(result.Files.Count, result.Telemetry.FilesEnumerated);
        Assert.Equal(result.Files.Count(f => f.IsPlaceholder), result.Telemetry.FilesPlaceholder);
    }

    [Fact]
    public void Enumerate_DeterministicOrder()
    {
#if !WINDOWS
        // Test marked with Trait("OS", "Windows") - does not compile on Linux.
        return;
#endif

        File.WriteAllText(Path.Combine(_root, "z.txt"), "z");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "m.txt"), "m");

        var result1 = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);
        var result2 = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

        var paths1 = result1.Files.Select(f => f.Path).ToArray();
        var paths2 = result2.Files.Select(f => f.Path).ToArray();

        Assert.Equal(paths1, paths2);
        Assert.Equal(new[] { "a.txt", "m.txt", "z.txt" }.Select(p => Path.Combine(_root, p)),
                     paths1.OrderBy(p => p, StringComparer.Ordinal));
    }

    private void CreateTreeFixture()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "B.txt"), "B");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "z.txt"), "sub/z");
    }
}

// P/Invoke helpers for tests.
#if WINDOWS
internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateSymbolicLinkW(string lpSymlinkFileName, string lpTargetFileName, bool bIsDirectory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteFileW(string lpFileName);
}
#endif