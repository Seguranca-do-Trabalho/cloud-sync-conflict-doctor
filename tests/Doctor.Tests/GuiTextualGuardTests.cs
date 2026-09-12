using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G2 (t_87f625aa) — extended GUIVM-02 textual guard (docs/test-strategy.md):
/// NO GUI string labels destruction as "Delete", "Erase", "Remove", or
/// synonym; the sole canonical label for actions on user content is
/// "Move to quarantine" (ADR-0002; §37 — unambiguous visual safety).
/// Complete scan: all .axaml files in GUI project + ViewModel string literals
/// (sources of user-visible text: Content, Text, Watermark).
/// </summary>
[Xunit.Trait("Category", "GuiVm")]
public class GuiTextualGuardTests
{
    private static readonly string[] ForbiddenWords =
    [
        // English destructive vocabulary (banned in GUI labels)
        "delete", "erase", "remove", "discard", "destroy", "wipe", "purge", "trash",
    ];

    /// <summary>Explicit exceptions: context that negates the destructive meaning.</summary>
    private static readonly string[] AllowedPhrases =
    [
        // The screen states that NOTHING is discarded — explicit negation, not destruction offer.
        "Nothing is discarded",
        "No files were discarded",
        "No content was discarded",
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

    private static IReadOnlyList<(string File, string Line)> InterfaceStrings()
    {
        var root = GuiRoot();
        var found = new List<(string, string)>();

        // 1) All .axaml files in the GUI (screens + resources): visible text attributes.
        foreach (var axaml in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(axaml))
            {
                lineNumber++;
                foreach (var value in ExtractValues(line,
                             "Content=\"", "Text=\"", "Watermark=\"", "Title=\""))
                {
                    found.Add(($"{Path.GetRelativePath(root, axaml)}:{lineNumber}", value));
                }
            }
        }

        // 2) String literals from ViewModels and code-behind (labels built in code).
        foreach (var cs in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(cs))
            {
                lineNumber++;
                foreach (var literal in ExtractCsLiterals(line))
                {
                    found.Add(($"{Path.GetRelativePath(root, cs)}:{lineNumber}", literal));
                }
            }
        }

        return found;
    }

    /// <summary>Extracts XAML text attribute values from the line.</summary>
    private static IEnumerable<string> ExtractValues(string line, params string[] attributes)
    {
        foreach (var attribute in attributes)
        {
            var start = 0;
            while (true)
            {
                var pos = line.IndexOf(attribute, start, StringComparison.Ordinal);
                if (pos < 0)
                {
                    break;
                }

                var open = pos + attribute.Length;
                var close = line.IndexOf('"', open);
                if (close > open)
                {
                    yield return line[open..close];
                }

                start = pos + attribute.Length;
            }
        }
    }

    /// <summary>Extracts string literals between single/double quotes in C# code.</summary>
    private static IEnumerable<string> ExtractCsLiterals(string line)
    {
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(
                     line, "\"((?:[^\"\\\\]|\\\\.)*)\"").Cast<System.Text.RegularExpressions.Match>())
        {
            if (m.Groups[1].Value.Length > 0)
            {
                yield return m.Groups[1].Value;
            }
        }
    }

    private static bool IsViolation(string text)
    {
        var allowed = AllowedPhrases.Any(f =>
            text.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (allowed)
        {
            return false;
        }

        return ForbiddenWords.Any(p =>
            text.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Canonical_destructive_label_is_move_to_quarantine()
    {
        var texts = InterfaceStrings();

        Assert.Contains(texts, t => t.Line.Contains("Move to quarantine", StringComparison.Ordinal));
    }

    [Fact]
    public void No_gui_string_uses_destructive_vocabulary()
    {
        var violations = InterfaceStrings()
            .Where(t => IsViolation(t.Line))
            .Select(t => $"{t.File}: \"{t.Line}\"")
            .ToList();

        Assert.True(violations.Count == 0,
            $"GUIVM-02 violated — forbidden destructive vocabulary found:\n{string.Join("\n", violations)}");
    }
}
