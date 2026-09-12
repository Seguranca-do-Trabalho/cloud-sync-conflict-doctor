using Xunit;

namespace Doctor.Tests;

/// <summary>
/// T06 — cycle 4 (RED): visual security rule of the card, verified against actual AXAML.
/// Destructive action is ALWAYS labeled "move to quarantine", never "delete" (ADR-0002);
/// primary button and destructive button have distinct classes and colors.
/// </summary>
public class VisualSecurityRuleTests
{
    private static string ReadMainWindowAxaml()
    {
        return ReadGuiFile("MainWindow.axaml");
    }

    private static string ReadAppAxaml()
    {
        return ReadGuiFile("App.axaml");
    }

    private static string ReadGuiFile(string fileName)
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var path = Path.Combine(dir!, "src", "Doctor.Gui", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void No_destructive_offer_uses_the_word_delete()
    {
        var axaml = ReadMainWindowAxaml();

        Assert.DoesNotContain("Apagar", axaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Excluir", axaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Delete", axaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_destructive_action_refers_to_moving_to_quarantine()
    {
        var axaml = ReadMainWindowAxaml();

        Assert.Contains("Quarantine", axaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Primary_and_destructive_buttons_have_distinct_colors()
    {
        // G2: style system lives in App.axaml (centralized); screens only consume classes.
        var app = ReadAppAxaml();
        Assert.Contains("Button.primary", app, StringComparison.Ordinal);
        Assert.Contains("Button.destructive", app, StringComparison.Ordinal);
        Assert.Matches(@"Button\.primary""[^/]*#1F6FEB", app);
        Assert.Matches(@"Button\.destructive""[^/]*#C93C37", app);

        // Screens apply classes on buttons.
        var axaml = ReadMainWindowAxaml();
        Assert.Contains("Classes=\"primary\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"destructive\"", axaml, StringComparison.Ordinal);
    }

    // --- G2 (t_87f625aa): named styles CENTRALIZED in App.axaml ---

    [Fact]
    public void Named_button_styles_are_centralized_in_App_axaml()
    {
        var app = ReadAppAxaml();

        // Primary (blue), destructive (red), and secondary (neutral) defined in App.axaml,
        // so all screens inherit the same visual vocabulary (ADR-0009, §37).
        Assert.Matches(@"Style\s+Selector=""Button\.primary""[\s\S]*?#1F6FEB", app);
        Assert.Matches(@"Style\s+Selector=""Button\.destructive""[\s\S]*?#C93C37", app);
        Assert.Contains("Button.secondary", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_button_on_all_screens_uses_class_vocabulary()
    {
        // G2: no screen may use an unclassed button — primary/destructive/secondary
        // vocabulary is unique and complete (ADR-0009, §37).
        var lines = ReadMainWindowAxaml().Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("<Button", StringComparison.Ordinal))
            {
                continue;
            }

            var usesVocabulary = lines[i].Contains("Classes=\"primary\"", StringComparison.Ordinal)
                || lines[i].Contains("Classes=\"destructive\"", StringComparison.Ordinal)
                || lines[i].Contains("Classes=\"secondary\"", StringComparison.Ordinal);

            Assert.True(usesVocabulary,
                $"Button without security class at line {i + 1}: {lines[i].Trim()}");
        }
    }

    [Fact]
    public void Destructive_affordance_is_not_shared_with_navigation_or_comparison()
    {
        // ADR-0009 (consequences) + §37: destructive cannot resemble compare/navigation.
        // Destructive button (class destructive) never carries navigation/comparison text,
        // and navigation/comparison buttons never use destructive class.
        var axaml = ReadMainWindowAxaml();

        foreach (var line in axaml.Split('\n'))
        {
            var hasDestructiveClass = line.Contains("Classes=\"destructive\"", StringComparison.Ordinal);
            var navigationLabel = line.Contains("Content=\"Compare", StringComparison.Ordinal)
                || line.Contains("Content=\"View ", StringComparison.Ordinal)
                || line.Contains("Content=\"Continue", StringComparison.Ordinal)
                || line.Contains("Content=\"Back", StringComparison.Ordinal)
                || line.Contains("Content=\"Rethink", StringComparison.Ordinal)
                || line.Contains("Content=\"Examine", StringComparison.Ordinal)
                || line.Contains("Content=\"Scan", StringComparison.Ordinal)
                || line.Contains("Content=\"Register", StringComparison.Ordinal);

            Assert.False(hasDestructiveClass && navigationLabel,
                $"Destructive affordance shared with navigation/comparison: {line.Trim()}");
        }
    }
}
