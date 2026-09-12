using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Doctor.Gui.Engine;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Summary screen (§15): answers the 5 questions from the schema v1 report.
/// Counts in raw digits (InvariantCulture); space with a fixed comma
/// decimal separator, independent of the machine locale (§3).
/// </summary>
public partial class SummaryViewModel : ObservableObject
{
    [ObservableProperty]
    private ScanReport? _report;

    // --- The 5 questions from §15, derived exclusively from report data ---

    /// <summary>1. Files found = files_enumerated.</summary>
    public string FilesFound =>
        (Report?.Telemetry.FilesEnumerated ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>2. Identical duplicates = redundant copies of each class.</summary>
    public string IdenticalDuplicates =>
        (Report?.IdenticalDuplicateCount ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>3. Real conflicts = groups with real content divergence.</summary>
    public string RealConflicts =>
        (Report?.RealConflicts.Count ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>4. Placeholders ignored = files_placeholder (never opened).</summary>
    public string PlaceholdersIgnored =>
        (Report?.Telemetry.FilesPlaceholder ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 5. Safely recoverable space: sum of sizes of items eligible for
    /// quarantine — identical duplicate losers + versions that will not be
    /// kept in real conflicts (explicitly tested formula).
    /// </summary>
    public string RecoverableSpace => FormatBytes(Report?.RecoverableBytes ?? 0);

    /// <summary>Human-readable units; fixed dot decimal separator, invariant locale.</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => WithDot(bytes / 1_073_741_824.0) + " GB",
        >= 1_048_576 => WithDot(bytes / 1_048_576.0) + " MB",
        >= 1_024 => WithDot(bytes / 1_024.0) + " KB",
        _ => $"{bytes} B",
    };

    private static string WithDot(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
