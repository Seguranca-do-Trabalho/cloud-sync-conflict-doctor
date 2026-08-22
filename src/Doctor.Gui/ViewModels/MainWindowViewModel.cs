using System.Collections.ObjectModel;
using System.Globalization;
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

    // --- Resumo §15: as 5 perguntas da primeira tela pós-scan ---
    // Contagens em dígitos crus (invariantes); espaço com separador decimal pt-BR fixo,
    // independente do locale da máquina (determinismo §3).

    public string SummaryFilesFound =>
        (Report?.Telemetry.FilesEnumerated ?? 0).ToString(CultureInfo.InvariantCulture);

    public string SummaryIdenticalDuplicates =>
        (Report?.IdenticalDuplicateCount ?? 0).ToString(CultureInfo.InvariantCulture);

    public string SummaryRealConflicts =>
        (Report?.RealConflicts.Count ?? 0).ToString(CultureInfo.InvariantCulture);

    public string SummaryPlaceholdersIgnored =>
        (Report?.Telemetry.FilesPlaceholder ?? 0).ToString(CultureInfo.InvariantCulture);

    public string SummaryRecoverableSpace => FormatBytes(Report?.RecoverableBytes ?? 0);

    public bool HasResults => Report is not null;

    partial void OnReportChanged(ScanReport? value)
    {
        OnPropertyChanged(nameof(SummaryFilesFound));
        OnPropertyChanged(nameof(SummaryIdenticalDuplicates));
        OnPropertyChanged(nameof(SummaryRealConflicts));
        OnPropertyChanged(nameof(SummaryPlaceholdersIgnored));
        OnPropertyChanged(nameof(SummaryRecoverableSpace));
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>Unidades legíveis; separador decimal vírgula fixo (pt-BR), nunca o locale da máquina.</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => ComVirgula(bytes / 1_073_741_824.0) + " GB",
        >= 1_048_576 => ComVirgula(bytes / 1_048_576.0) + " MB",
        >= 1_024 => ComVirgula(bytes / 1_024.0) + " KB",
        _ => $"{bytes} B",
    };

    private static string ComVirgula(double valor) =>
        valor.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');

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

    /// <summary>Enfileira caminho para mover para a quarentena (ADR-0002), sem duplicar.</summary>
    internal void QueueForQuarantineInternal(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath) && !QuarantineQueue.Contains(filePath))
        {
            QuarantineQueue.Add(filePath);
            OnPropertyChanged(nameof(QuarantineCount));
            ConfirmQuarantineCommand.NotifyCanExecuteChanged();
        }
    }

    private bool HasQuarantineItems() => QuarantineQueue.Count > 0;

    /// <summary>Executa a movimentação. No esqueleto, o motor falso não toca em arquivo
    /// real: apenas registra a fila e avança para a tela de quarentena.</summary>
    [RelayCommand(CanExecute = nameof(HasQuarantineItems))]
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
