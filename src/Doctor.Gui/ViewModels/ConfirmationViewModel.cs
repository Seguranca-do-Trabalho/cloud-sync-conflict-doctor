using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Sub-ViewModel da tela Confirmação (§15): registra a movimentação concluída
/// para a quarentena. Segurança §2/ADR-0002: nada é descartado — a mensagem
/// fala sempre em quarentena e restauração, nunca em apagar ou deletar.
/// </summary>
public partial class ConfirmationViewModel : ObservableObject
{
    [ObservableProperty]
    private int _itemsMoved;

    /// <summary>Contagem em dígitos crus, nunca locale (§3).</summary>
    public string ItemsMovedLabel => ItemsMoved.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Mensagem de segurança: todo item permanece na quarentena, recuperável
    /// pelo manifesto. Estado zero também seguro e explícito.
    /// </summary>
    public string SafetyMessage => ItemsMoved == 0
        ? "Nenhuma movimentação registrada. Nada sai da pasta do usuário sem passar pela quarentena."
        : "Itens movidos para a quarentena. Nenhum conteúdo foi descartado: tudo permanece na quarentena e pode ser restaurado pelo manifesto.";

    /// <summary>Registra a conclusão: a fila transferida vira contagem de itens movidos.</summary>
    public void Complete(IReadOnlyList<string> queuedPaths)
    {
        ItemsMoved = queuedPaths.Count;
    }

    partial void OnItemsMovedChanged(int value)
    {
        OnPropertyChanged(nameof(ItemsMovedLabel));
        OnPropertyChanged(nameof(SafetyMessage));
    }
}
