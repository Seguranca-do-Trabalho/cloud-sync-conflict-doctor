using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Quarantine screen sub-ViewModel: queue of items marked to be moved to
/// quarantine (ADR-0002), without duplicating repeated items. Count in raw digits
/// (InvariantCulture). MainWindow only orchestrates; no real file is touched.
/// </summary>
public partial class QuarantineViewModel : ObservableObject
{
    public ObservableCollection<string> Paths { get; } = [];

    public int Count => Paths.Count;

    public bool HasItems => Count > 0;

    /// <summary>Count in raw digits, never locale (§3).</summary>
    public string CountLabel => Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Enqueues a path for quarantine move, without duplicating.</summary>
    public void Queue(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath) && !Paths.Contains(filePath))
        {
            Paths.Add(filePath);
            NotifyCounts();
        }
    }

    /// <summary>Empties the queue (new scan starts from scratch).</summary>
    public void Clear()
    {
        Paths.Clear();
        NotifyCounts();
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountLabel));
    }
}
