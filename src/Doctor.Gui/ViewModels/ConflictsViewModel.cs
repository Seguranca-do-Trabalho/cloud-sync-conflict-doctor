using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Doctor.Gui.Engine;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Tela Conflitos reais (§15): grupos com divergência real de conteúdo do relatório
/// schema v1, cada linha expondo a sugestão determinística de versão a manter
/// (SPEC §17: mtime → size → path em bytes UTF-8) e os bytes elegíveis a quarentena.
/// </summary>
public partial class ConflictsViewModel : ObservableObject
{
    [ObservableProperty]
    private ScanReport? _report;

    /// <summary>Linhas prontas para binding: um wrapper por grupo do relatório.</summary>
    public ObservableCollection<ConflictGroupRow> Groups => Report is null
        ? []
        : [.. Report.RealConflicts.Select(g => new ConflictGroupRow(g))];
}

/// <summary>Linha da tela Conflitos: um grupo com divergência real.</summary>
public partial class ConflictGroupRow : ObservableObject
{
    private readonly ConflictGroup _group;

    public ConflictGroupRow(ConflictGroup group) => _group = group;

    public ConflictGroup Group => _group;

    public string BaseName => _group.BaseName;

    /// <summary>Número de versões divergentes do grupo.</summary>
    public int VersionCount => _group.Versions.Count;

    /// <summary>Tamanho comum do grupo (schema §6.2), legível, pt-BR fixo.</summary>
    public string TotalSizeLabel => MainWindowViewModel.FormatBytes(_group.TotalBytes);

    /// <summary>
    /// Versão sugerida a manter: mtime mais recente → maior size → menor caminho
    /// em bytes UTF-8. Nunca "first seen" (determinismo §3).
    /// </summary>
    public string SuggestedKeepPath => SuggestedKeep().Path;

    /// <summary>
    /// Bytes elegíveis para quarentena neste grupo: soma dos tamanhos das versões
    /// que NÃO serão mantidas (fórmula do card, testada explicitamente).
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
