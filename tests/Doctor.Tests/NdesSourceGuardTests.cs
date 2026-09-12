using System.Text.RegularExpressions;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// NDES-05 (t_b126bdf6) — static guard over product source text.
/// NO .cs under src/ (excluded tests/, obj/, bin/) may contain permanent deletion
/// APIs: File.Delete, Directory.Delete, DeleteFile/DeleteFileW (P/Invoke),
/// FILE_DISPOSITION_INFO or SetFileInformationByHandle(FileDispositionInfo*),
/// FILE_RENAME_INFO (except quarantine move, which does not yet exist in v1).
/// Empty allowlist in v1 (per orchestrator decision).
/// Complements ADR-0002 item 5 and reviewer static review (GATE 3).
/// </summary>
[Xunit.Trait("Category", "Safety")]
public class NdesSourceGuardTests
{
    // Rules: each regex must capture dangerous call.
    // "api" group identifies which rule fired for the failure message.
    private static readonly Regex[] Patterns =
    [
        // Direct managed APIs.
        new Regex(@"\bFile\.Delete\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"\bDirectory\.Delete\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // P/Invoke Windows (delete file/directory).
        new Regex(@"\bDeleteFile\s*[W]?\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"\bDeleteFileW\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // FILE_DISPOSITION_INFO / SetFileInformationByHandle(FileDispositionInfo*).
        new Regex(@"\bFILE_DISPOSITION_INFO\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(
            @"SetFileInformationByHandle\s*\([^,]+,\s*FileDispositionInfo\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // FILE_RENAME_INFO (rename is only allowed within quarantine move,
        // not yet implemented in v1; empty allowlist).
        new Regex(@"\bFILE_RENAME_INFO\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    private static string SrcRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(dir!, "src");
    }

    private static IReadOnlyList<string> ScanTexts()
    {
        var root = SrcRoot();
        var violations = new List<string>();

        if (!Directory.Exists(root))
        {
            return ["src/ does not exist (repo structure changed?)"];
        }

        foreach (var cs in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            // Excludes code-generated, obj and bin.
            if (cs.Contains("/obj/", StringComparison.Ordinal) ||
                cs.Contains("\\obj\\", StringComparison.Ordinal) ||
                cs.Contains("/bin/", StringComparison.Ordinal) ||
                cs.Contains("\\bin\\", StringComparison.Ordinal))
            {
                continue;
            }

            var relPath = Path.GetRelativePath(root, cs);
            var lineNumber = 0;
            foreach (var line in File.ReadLines(cs))
            {
                lineNumber++;
                foreach (var pattern in Patterns)
                {
                    if (pattern.IsMatch(line))
                    {
                        violations.Add($"{relPath}:{lineNumber} [{pattern}] {line.Trim()}");
                    }
                }
            }
        }

        return violations;
    }

    [Fact]
    public void Zero_occurrences_of_delete_api_in_src_source_text()
    {
        var violations = ScanTexts();
        Assert.True(violations.Count == 0,
            $"NDES-05 violated — permanent deletion APIs found in src/. " +
            $"Empty allowlist in v1.\n{string.Join("\n", violations)}");
    }

    /// <summary>Double proof: shows detector recognizes and rejects real violations.</summary>
    [Theory]
    [InlineData("File.Delete(path);")]
    [InlineData("Directory.Delete(dir, true);")]
    [InlineData("DeleteFileW(name);")]
    [InlineData("var fi = new FILE_DISPOSITION_INFO();")]
    [InlineData("SetFileInformationByHandle(h, FileDispositionInfo, ...);")]
    [InlineData("var ri = new FILE_RENAME_INFO();")]
    public void Detector_catches_violation_in_contaminated_snippet(string snippet)
    {
        // Injects contaminated snippet into temporary file under src/
        var root = SrcRoot();
        var fake = Path.Combine(root, "__guard_proof_nothis_file_21847.cs");
        try
        {
            File.WriteAllText(fake, snippet);
            var violations = ScanTexts();
            Assert.NotEmpty(violations);
            // Message shows path relative to src/ + line number.
            Assert.Contains("__guard_proof_nothis_file_21847.cs:",
                string.Join(" ", violations));
        }
        finally
        {
            // Remove immediately; clean commit NDES-05 passes.
            if (File.Exists(fake))
            {
                File.Delete(fake);
            }
        }
    }
}
