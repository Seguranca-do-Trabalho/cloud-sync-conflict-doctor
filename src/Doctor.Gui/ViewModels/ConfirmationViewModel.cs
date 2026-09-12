using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Confirmation screen sub-ViewModel (§15): records the completed move
/// to quarantine. Safety §2/ADR-0002: nothing is discarded — the message
/// always speaks of quarantine and restoration, never of deleting or removing.
/// </summary>
public partial class ConfirmationViewModel : ObservableObject
{
    [ObservableProperty]
    private int _itemsMoved;

    /// <summary>Count in raw digits, never locale (§3).</summary>
    public string ItemsMovedLabel => ItemsMoved.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Safety message: every item remains in quarantine, recoverable
    /// via the manifest. Zero state is also safe and explicit.
    /// </summary>
    public string SafetyMessage => ItemsMoved == 0
        ? "No moves recorded. Nothing leaves the user's folder without going through quarantine."
        : "Items moved to quarantine. No content was discarded: everything remains in quarantine and can be restored from the manifest.";

    /// <summary>Records completion: the transferred queue becomes the moved-items count.</summary>
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
