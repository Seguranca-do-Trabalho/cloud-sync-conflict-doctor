using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Doctor.Gui.Engine;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Real conflicts screen (§15): groups with real content divergence from the schema v1
/// report, each row exposing the deterministic version-to-keep suggestion
/// (SPEC §17: mtime → size → path in UTF-8 bytes) and the quarantine-eligible bytes.
/// </summary>
public partial class ConflictsViewModel : ObservableObject
{
    [ObservableProperty]
    private ScanReport? _report;

    /// <summary>Rows ready for binding: one wrapper per report group.</summary>
    public ObservableCollection<ConflictGroupRow> Groups => Report is null
        ? []
        : [.. Report.RealConflicts.Select(g => new ConflictGroupRow(g))];
}

/// <summary>Conflicts screen row: one group with real divergence.</summary>
public partial class ConflictGroupRow : ObservableObject
{
    private readonly ConflictGroup _group;

    public ConflictGroupRow(ConflictGroup group) => _group = group;

    public ConflictGroup Group => _group;

    public string BaseName => _group.BaseName;

    /// <summary>Number of divergent versions in the group.</summary>
    public int VersionCount => _group.Versions.Count;

    /// <summary>Common group size (schema §6.2), human-readable, fixed pt-BR.</summary>
    public string TotalSizeLabel => MainWindowViewModel.FormatBytes(_group.TotalBytes);

    /// <summary>
    /// Suggested version to keep: newest mtime → largest size → shortest path
    /// in UTF-8 bytes. Never "first seen" (determinism §3).
    /// </summary>
    public string SuggestedKeepPath => SuggestedKeep().Path;

    /// <summary>
    /// Bytes eligible for quarantine in this group: sum of sizes of versions
    /// that will NOT be kept (card formula, explicitly tested).
    /// </summary>
    public long BytesToQuarantine =>
        _group.SumVersionsBytes() - SuggestedKeep().SizeBytes;

    private ConflictVersion SuggestedKeep() =>
        _group.Versions
            .OrderByDescending(v => v.MtimeUtc.UtcTicks)
            .ThenByDescending(v => v.SizeBytes)
            .ThenBy(v => Encoding.UTF8.GetBytes(v.Path),
                Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b.AsSpan())))
            .First();
}
