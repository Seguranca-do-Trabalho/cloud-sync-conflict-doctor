using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G2 (t_87f625aa) — "Choose Action" screen: FIXED presentation of the 4 §17 strategies
/// ('keep newest', 'keep largest', 'keep version from specific machine',
/// 'choose manually') with the tiebreak rule visible (mtime → size → path, §17).
/// PRESENTATION ONLY: resolution logic belongs to EPIC 07 (scheduled).
/// Documented extension point — nothing here decides which version to keep beyond the
/// existing deterministic suggestion (mtime→size→path).
/// </summary>
public class StrategiesScreenTests
{
    private static MainWindowViewModel NewVm() => new(new FakeScanEngine());

    [Fact]
    public void Strategy_list_is_fixed_with_four_options_in_spec_order()
    {
        var strategies = MainWindowViewModel.ResolutionStrategies;

        Assert.Equal(4, strategies.Count);
        Assert.Equal(
        [
            "Keep newest",
            "Keep largest",
            "Keep version from specific machine",
            "Choose manually",
        ], strategies);
    }

    [Fact]
    public void Tiebreak_rule_is_visible_and_mtime_size_path()
    {
        Assert.Equal("mtime → size → path", MainWindowViewModel.TiebreakRule);
    }

    [Fact]
    public void Strategies_are_displayed_on_choose_action_screen()
    {
        // The Choose Action screen presents the CANONICAL list from the ViewModel (single source,
        // no duplicated literals) and displays the §17 tiebreak rule.
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var path = Path.Combine(dir!, "src", "Doctor.Gui", "MainWindow.axaml");
        var axaml = File.ReadAllText(path);

        Assert.Contains("ItemsSource=\"{x:Static vm:MainWindowViewModel.ResolutionStrategies}\"",
            axaml, StringComparison.Ordinal);
        Assert.Contains(MainWindowViewModel.TiebreakRule, axaml, StringComparison.Ordinal);
    }
}
