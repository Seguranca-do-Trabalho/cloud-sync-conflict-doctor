using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Sub-ViewModel da tela Quarentena: fila de itens marcados para mover para a
/// quarentena (ADR-0002), sem duplicar item repetido. Contagem em dígitos crus
/// (InvariantCulture). A MainWindow apenas orquestra; nenhum arquivo real é tocado.
/// </summary>
public partial class QuarantineViewModel : ObservableObject
{
    public ObservableCollection<string> Paths { get; } = [];

    public int Count => Paths.Count;

    public bool HasItems => Count > 0;

    /// <summary>Contagem em dígitos crus, nunca locale (§3).</summary>
    public string CountLabel => Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Enfileira caminho para mover para a quarentena, sem duplicar.</summary>
    public void Queue(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath) && !Paths.Contains(filePath))
        {
            Paths.Add(filePath);
            NotificarContagens();
        }
    }

    /// <summary>Esvazia a fila (novo scan começa do zero).</summary>
    public void Clear()
    {
        Paths.Clear();
        NotificarContagens();
    }

    private void NotificarContagens()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountLabel));
    }
}
