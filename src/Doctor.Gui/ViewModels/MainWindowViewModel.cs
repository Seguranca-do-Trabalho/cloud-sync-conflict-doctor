using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Doctor.Gui.Engine;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Fluxo mínimo do §15 da SPEC: Escolher pasta → Escanear → Resumo → Duplicatas
/// → Conflitos reais → Comparar → Escolher ação → Quarentena → Confirmação.
/// Navegação por tela corrente; o esqueleto prova o fluxo ponta a ponta com motor falso.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IScanEngine _engine;

    public MainWindowViewModel(IScanEngine engine) => _engine = engine;

    public enum Screen
    {
        ChooseFolder,
        Scanning,
        Summary,
        Duplicates,
        Conflicts,
        Compare,
        ChooseAction,
        Quarantine,
        Confirmation,
    }

    [ObservableProperty]
    private string _chosenFolder = "";

    [ObservableProperty]
    private int _scanProgressPercent;

    [ObservableProperty]
    private Screen _currentScreen = Screen.ChooseFolder;

    [ObservableProperty]
    private ScanReport? _report;

    [ObservableProperty]
    private ConflictGroup? _selectedConflict;

    /// <summary>Tela Comparar: versão que o usuário decide manter.</summary>
    [ObservableProperty]
    private ConflictVersion? _selectedVersionToKeep;

    /// <summary>Fila de itens marcados para mover para a quarentena.</summary>
    public ObservableCollection<string> QuarantineQueue { get; } = [];

    public int QuarantineCount => QuarantineQueue.Count;

    [RelayCommand]
    private void StartScan()
    {
        Report = null;
        ScanProgressPercent = 0;
        QuarantineQueue.Clear();
        CurrentScreen = Screen.Scanning;

        // Motor falso: execução síncrona; o progresso é simulado pelo próprio motor.
        Report = _engine.Scan(ChosenFolder, p => ScanProgressPercent = p);

        SelectedConflict = Report.RealConflicts.FirstOrDefault();
        SelectedVersionToKeep = SuggestVersionToKeep(SelectedConflict);
        CurrentScreen = Screen.Summary;
    }

    /// <summary>Sugestão determinística (ADR-0003): mtime → size → path; nunca "first seen".
    /// Caminho comparado em bytes UTF-8, nunca locale.</summary>
    internal static ConflictVersion? SuggestVersionToKeep(ConflictGroup? group) =>
        group?.Versions
            .OrderByDescending(v => v.MtimeUtc.UtcTicks)
            .ThenByDescending(v => v.SizeBytes)
            .ThenBy(v => Encoding.UTF8.GetBytes(v.Path),
                    Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b.AsSpan())))
            .FirstOrDefault();

    [RelayCommand]
    private void OpenDuplicates() => CurrentScreen = Screen.Duplicates;

    [RelayCommand]
    private void OpenConflicts() => CurrentScreen = Screen.Conflicts;

    /// <summary>Tela Conflitos: abre a comparação do grupo escolhido.</summary>
    [RelayCommand]
    private void CompareConflict(ConflictGroup? group)
    {
        SelectedConflict = group ?? Report?.RealConflicts.FirstOrDefault();
        SelectedVersionToKeep = SuggestVersionToKeep(SelectedConflict);
        CurrentScreen = Screen.Compare;
    }

    /// <summary>Comparar: enfileira todas as versões exceto a que será mantida.
    /// Única operação sobre conteúdo do usuário: mover para quarentena (ADR-0002).</summary>
    [RelayCommand]
    private void QueueOtherVersionsForQuarantine()
    {
        foreach (var version in SelectedConflict?.Versions ?? [])
        {
            if (!ReferenceEquals(version, SelectedVersionToKeep))
            {
                if (!QuarantineQueue.Contains(version.Path))
                {
                    QuarantineQueue.Add(version.Path);
                }
            }
        }

        CurrentScreen = Screen.ChooseAction;
    }

    [RelayCommand]
    private void ConfirmQuarantine() => CurrentScreen = Screen.Quarantine;

    [RelayCommand]
    private void OpenConfirmationFromQuarantine() => CurrentScreen = Screen.Confirmation;

    [RelayCommand]
    private void Restart() => CurrentScreen = Screen.ChooseFolder;

    [RelayCommand]
    private void GoBack() => CurrentScreen = CurrentScreen switch
    {
        Screen.Duplicates => Screen.Summary,
        _ => Screen.ChooseFolder,
    };
}
