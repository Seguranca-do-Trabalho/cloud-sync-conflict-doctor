using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Doctor.Gui.Engine;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Duplicates screen (§15): identical duplicate classes from the schema v1 report,
/// with the kept copy highlighted and the redundant copy count.
/// Counts in raw digits (InvariantCulture); sizes with a fixed pt-BR separator.
/// </summary>
public partial class DuplicatesViewModel : ObservableObject
{
    [ObservableProperty]
    private ScanReport? _report;

    /// <summary>Rows ready for binding: one wrapper per report class.</summary>
    public ObservableCollection<DuplicateGroupRow> Groups => Report is null
        ? []
        : [.. Report.IdenticalDuplicates.Select(g => new DuplicateGroupRow(g))];

    /// <summary>Redundant copies = members of each class minus the kept one.</summary>
    public int RedundantCopies => Report?.IdenticalDuplicateCount ?? 0;

    /// <summary>Count in raw digits, never locale.</summary>
    public string RedundantCopiesLabel =>
        RedundantCopies.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Duplicates screen row: one identical duplicate class.</summary>
public partial class DuplicateGroupRow : ObservableObject
{
    private readonly DuplicateGroup _group;

    public DuplicateGroupRow(DuplicateGroup group) => _group = group;

    public DuplicateGroup Group => _group;

    /// <summary>Kept path of the class: shortest path in UTF-8 bytes (first in the sorted list).</summary>
    public string KeptPath => _group.Files[0];

    /// <summary>Common size of the class, human-readable; fixed comma decimal separator (pt-BR).</summary>
    public string SizeLabel => MainWindowViewModel.FormatBytes(_group.SizeBytes);

    /// <summary>Class hash, BLAKE3 label ready for display.</summary>
    public string HashLabel => $"BLAKE3 {_group.Blake3Hash}";
}
