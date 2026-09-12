using System.Text.RegularExpressions;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G3 (t_8d08c08b) — structural guard complementary to NDES-05 (which covers src/**):
/// NO GUI code invokes direct deletion APIs (File.Delete, Directory.Delete,
/// destructive Move/Replace, removal P/Invoke) — neither in commands nor in code-behind.
/// The GUI only talks to IScanEngine/IQuarantineService-like interfaces;
/// physical movement to quarantine is the quarantine service's responsibility, never GUI.
/// </summary>
public class GuiAntiDeleteGuardTests
{
    /// <summary>Patterns characterizing direct destructive calls on the filesystem
    /// from the GUI project.</summary>
    private static readonly string[] ForbiddenPatterns =
    [
        @"\bFile\.Delete\s*\(",
        @"\bDirectory\.Delete\s*\(",
        @"\bFileSystem\.DeleteFile\s*\(",
        @"\bFileSystem\.DeleteDirectory\s*\(",
        @"\.Delete\s*\(\s*\)",                       // FileInfo.Delete()/DirectoryInfo.Delete()
        @"\bFile\.Replace\s*\(",
        @"\bFile\.Move\s*\(",                        // moving file is quarantine service operation
        @"\.MoveTo\s*\(",                            // FileInfo.MoveTo()/DirectoryInfo.MoveTo()
        @"\bDirectory\.Move\s*\(",
        @"\bSHFileOperation\b",
        @"\bIFileOperation\b",
        @"\bDllImport\b",                            // GUI DEMO makes zero P/Invoke
    ];

    /// <summary>Explicit exceptions: legitimate calls that do not destroy content.</summary>
    private static readonly string[] Exceptions =
    [
        // Directory.Move is forbidden above; nothing else matches these phrases:
        "Paths.Clear()",                              // VM internal queue (ObservableCollection)
        ".Items.Clear()",                             // presentation collections
    ];

    private static string GuiRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir!, "src", "Doctor.Gui");
    }

    [Fact]
    public void No_gui_code_invokes_direct_deletion_api()
    {
        var root = GuiRoot();
        var violations = new List<string>();

        foreach (var cs in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(cs))
            {
                lineNumber++;
                foreach (var pattern in ForbiddenPatterns)
                {
                    if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                    {
                        violations.Add(
                            $"{Path.GetRelativePath(root, cs)}:{lineNumber} [{pattern}] {line.Trim()}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Anti-delete guard violated — GUI never touches user files " +
            $"directly (movement belongs to quarantine service):\n{string.Join("\n", violations)}");
    }

    /// <summary>Mutation validation: guard must fail if someone inserts a
    /// deletion. Runs same detector over contaminated snippet — proves
    /// pattern recognizes the API, and green test is not accidental.</summary>
    [Theory]
    [InlineData("File.Delete(path);")]
    [InlineData("Directory.Delete(folder, recursive: true);")]
    [InlineData("source.MoveTo(destination);")]
    [InlineData("[DllImport(\"shell32.dll\")] static extern int SHFileOperation(...);")]
    public void Detector_recognizes_destructive_calls_in_contaminated_snippet(string snippet)
    {
        var contaminated = ForbiddenPatterns.Any(p =>
            Regex.IsMatch(snippet, p, RegexOptions.IgnoreCase));

        Assert.True(contaminated,
            $"Anti-delete guard detector did not recognize snippet: {snippet}");
    }
}
