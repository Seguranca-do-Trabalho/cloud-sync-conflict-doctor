using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Doctor.Gui.Engine;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Tela Duplicatas (§15): classes de duplicatas idênticas do relatório schema v1,
/// com a cópia mantida destacada e a contagem de cópias redundantes.
/// Contagens em dígitos crus (InvariantCulture); tamanhos com separador pt-BR fixo.
/// </summary>
public partial class DuplicatesViewModel : ObservableObject
{
    [ObservableProperty]
    private ScanReport? _report;

    /// <summary>Linhas prontas para binding: um wrapper por classe do relatório.</summary>
    public ObservableCollection<DuplicateGroupRow> Groups => Report is null
        ? []
        : [.. Report.IdenticalDuplicates.Select(g => new DuplicateGroupRow(g))];

    /// <summary>Cópias redundantes = membros de cada classe menos a mantida.</summary>
    public int RedundantCopies => Report?.IdenticalDuplicateCount ?? 0;

    /// <summary>Contagem em dígitos crus, nunca locale.</summary>
    public string RedundantCopiesLabel =>
        RedundantCopies.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Linha da tela Duplicatas: uma classe de duplicatas idênticas.</summary>
public partial class DuplicateGroupRow : ObservableObject
{
    private readonly DuplicateGroup _group;

    public DuplicateGroupRow(DuplicateGroup group) => _group = group;

    public DuplicateGroup Group => _group;

    /// <summary>Caminho mantido da classe: menor caminho em bytes UTF-8 (primeiro da lista ordenada).</summary>
    public string KeptPath => _group.Files[0];

    /// <summary>Tamanho comum da classe, legível; separador decimal vírgula fixa pt-BR.</summary>
    public string SizeLabel => MainWindowViewModel.FormatBytes(_group.SizeBytes);

    /// <summary>Hash da classe, rótulo BLAKE3 pronto para exibição.</summary>
    public string HashLabel => $"BLAKE3 {_group.Blake3Hash}";
}
