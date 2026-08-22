using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Doctor.Gui.Engine;
using Doctor.Gui.Flow;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Fluxo mínimo do §15 da SPEC: Escolher pasta → Escanear → Resumo → Duplicatas
/// → Conflitos reais → Comparar → Escolher ação → Quarentena → Confirmação.
///
/// G3 (t_8d08c08b): TODA a navegação é DELEGADA à <see cref="FlowStateMachine"/>
/// (pura, sem Avalonia). Aqui restam apresentação e orquestração: cada comando
/// espelha os dados visíveis nas guardas da máquina e dispara o gatilho — nenhum
/// comando muda de tela por conta própria.
///
/// DECISÃO DOCUMENTADA (lança × fica):
/// - Gatilho de avanço disparado fora da ordem válida LANÇA
///   TransicaoInvalidaException: um clique que atravessa o fluxo é bug de ligação
///   de botão, e silenciá-lo esconderia o erro (corretidade precede UX na SPEC).
/// - O botão Voltar consulta CanFire e PERMANECE onde está quando o mapa da
///   máquina bloqueia (Escolher pasta, Escaneando, Resumo e Confirmação):
///   comportamento equivalente a botão desabilitado — voltar jamais surpreende.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IScanEngine _engine;

    /// <summary>Máquina de estados do fluxo §15 — única autoridade de navegação.</summary>
    private readonly FlowStateMachine _flow = new();

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

    // --- Sub-ViewModels por tela; aqui restam só navegação e orquestração ---

    /// <summary>Tela Quarentena: fila de itens marcados para mover.</summary>
    public QuarantineViewModel Quarantine { get; } = new();

    /// <summary>Tela Confirmação: registro da movimentação concluída.</summary>
    public ConfirmationViewModel Confirmation { get; } = new();

    public MainWindowViewModel(IScanEngine engine)
    {
        _engine = engine;

        // O comando de confirmação é orquestração da MainWindow, mas o estado
        // (fila) é da sub-VM: CanExecute acompanha a contagem dela.
        Quarantine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QuarantineViewModel.HasItems)
                                 or nameof(QuarantineViewModel.Count))
            {
                ConfirmQuarantineCommand.NotifyCanExecuteChanged();
                OpenConfirmationFromQuarantineCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>Mapeamento FlowScreen → Screen desta VM (mesmos nove valores).</summary>
    private static Screen Mapear(FlowScreen tela) => tela switch
    {
        FlowScreen.ChooseFolder => Screen.ChooseFolder,
        FlowScreen.Scanning => Screen.Scanning,
        FlowScreen.Summary => Screen.Summary,
        FlowScreen.Duplicates => Screen.Duplicates,
        FlowScreen.Conflicts => Screen.Conflicts,
        FlowScreen.Compare => Screen.Compare,
        FlowScreen.ChooseAction => Screen.ChooseAction,
        FlowScreen.Quarantine => Screen.Quarantine,
        FlowScreen.Confirmation => Screen.Confirmation,
        _ => throw new ArgumentOutOfRangeException(nameof(tela), tela, null),
    };

    /// <summary>
    /// Espelha os dados visíveis da análise nas guardas da máquina. Chamado no
    /// início de TODO comando de navegação: a máquina decide sempre sobre o
    /// estado real da análise, nunca sobre bandeira velha.
    /// operation_id (§18): o id fake DERIVA do conteúdo da fila — sem fila não
    /// há id atribuível,logo Confirmação exige fila por construção.
    /// </summary>
    private void EspelharGuardas()
    {
        _flow.HasReport = Report is not null;
        _flow.HasSelectedConflict = SelectedConflict is not null;
        _flow.HasQueuedItems = Quarantine.HasItems;
        _flow.HasOperationId = Quarantine.HasItems;
    }

    // --- Tela "Escolher ação": estratégias do §17 como APRESENTAÇÃO VISUAL ---
    // Lista FIXA e ordenada conforme docs/SPEC.md §17. Somente apresentação: a
    // lógica de resolução por estratégia é do EPIC 07 (Resolution Engine, scheduled).
    // PONTO DE EXTENSÃO: quando o EPIC 07 entregar IResolutionStrategy, esta tela
    // passa a receber as opções do motor; os rótulos aqui são o contrato visual.
    public static IReadOnlyList<string> EstrategiasResolucao { get; } =
    [
        "Manter mais recente",
        "Manter maior",
        "Manter versão de determinada máquina",
        "Escolher manualmente",
    ];

    /// <summary>Regra obrigatória de desempate do §17, exibida ao usuário.</summary>
    public static string RegraDeEmpate => "mtime → size → path";

    // --- Tela "Confirmação": manifesto fake determinístico (§18) ---
    // Timestamp e id derivados de forma DETERMINÍSTICA (sem relógio local, sem locale):
    // o esqueleto usa a âncora fixa do motor DEMO. Quando EPIC 08 entregar a
    // quarentena real, o manifesto passa a vir do motor — os rótulos permanecem.

    /// <summary>Âncora temporal fixa do esqueleto DEMO (nunca DateTime.Now).</summary>
    internal static DateTimeOffset AncoraDemo { get; } =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Caminho previsto §18: ConflictDoctor/quarantine/<timestamp>.</summary>
    public static string CaminhoQuarentenaPrevisto()
    {
        var t = AncoraDemo;
        return $"ConflictDoctor/quarantine/{t.Year:D4}-{t.Month:D2}-{t.Day:D2}T{t.Hour:D2}-{t.Minute:D2}";
    }

    /// <summary>operation_id fake determinístico: hash estável do conteúdo da fila,
    /// em minúsculas hexadecimais (invariante de locale), prefixado para legibilidade.</summary>
    public string OperationIdPrevisto
    {
        get
        {
            var conteudo = string.Join("\n", Quarantine.Paths);
            var bytes = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(conteudo));
            var hex = Convert.ToHexString(bytes).ToLowerInvariant();
            return $"op-{hex[..16]}";
        }
    }

    /// <summary>Contagem exibida na Confirmação antes de habilitar o botão final.</summary>
    public int ConfirmacaoContagemItens => Quarantine.Count;

    /// <summary>Rótulo do caminho previsto §18 para a tela Confirmação (x:Static).</summary>
    public static string CaminhoQuarentenaPrevistoLabel =>
        $"destino previsto: {CaminhoQuarentenaPrevisto()}";

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

    // --- Visibilidade por tela (evita conversor enum→bool no XAML compilado) ---

    public bool ShowChooseFolder => CurrentScreen == Screen.ChooseFolder;
    public bool ShowScanning => CurrentScreen == Screen.Scanning;
    public bool ShowSummary => CurrentScreen == Screen.Summary;
    public bool ShowDuplicates => CurrentScreen == Screen.Duplicates;
    public bool ShowConflicts => CurrentScreen == Screen.Conflicts;
    public bool ShowCompare => CurrentScreen == Screen.Compare;
    public bool ShowChooseAction => CurrentScreen == Screen.ChooseAction;
    public bool ShowQuarantine => CurrentScreen == Screen.Quarantine;
    public bool ShowConfirmation => CurrentScreen == Screen.Confirmation;

    partial void OnCurrentScreenChanged(Screen value)
    {
        OnPropertyChanged(nameof(ShowChooseFolder));
        OnPropertyChanged(nameof(ShowScanning));
        OnPropertyChanged(nameof(ShowSummary));
        OnPropertyChanged(nameof(ShowDuplicates));
        OnPropertyChanged(nameof(ShowConflicts));
        OnPropertyChanged(nameof(ShowCompare));
        OnPropertyChanged(nameof(ShowChooseAction));
        OnPropertyChanged(nameof(ShowQuarantine));
        OnPropertyChanged(nameof(ShowConfirmation));
    }

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

    /// <summary>
    /// Caminho comum dos comandos simples: espelha os dados visíveis nas guardas,
    /// dispara o gatilho na máquina e publica a tela resultante. Comandos que
    /// precisam ajustar uma guarda entre o espelho e o disparo (Comparar,
    /// enfileirar) ou tratar recusa (Voltar) fazem os passos à mão.
    /// </summary>
    private void Navegar(FlowTrigger gatilho)
    {
        EspelharGuardas();
        _flow.Fire(gatilho);
        CurrentScreen = Mapear(_flow.Current);
    }

    [RelayCommand]
    private void StartScan()
    {
        // Recusado fora de Escolher pasta: reinício só pelo caminho válido (Restart).
        _flow.Fire(FlowTrigger.StartScan);

        // Nova análise começa do zero — nenhum resíduo da execução anterior.
        Report = null;
        ScanProgressPercent = 0;
        Quarantine.Clear();
        Confirmation.ItemsMoved = 0;
        CurrentScreen = Mapear(_flow.Current);   // Escaneando

        // Motor falso: execução síncrona; o progresso é simulado pelo próprio motor.
        Report = _engine.Scan(ChosenFolder, p => ScanProgressPercent = p);

        SelectedConflict = Report.RealConflicts.FirstOrDefault();
        SelectedVersionToKeep = SuggestVersionToKeep(SelectedConflict);

        EspelharGuardas();
        _flow.Fire(FlowTrigger.ScanCompleted);   // só sai de Escanear com relatório completo
        CurrentScreen = Mapear(_flow.Current);   // Resumo
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
    private void OpenDuplicates()
    {
        EspelharGuardas();
        _flow.Fire(FlowTrigger.OpenDuplicates);  // exige relatório presente
        CurrentScreen = Mapear(_flow.Current);
    }

    [RelayCommand]
    private void OpenConflicts()
    {
        EspelharGuardas();
        _flow.Fire(FlowTrigger.OpenConflicts);   // exige relatório presente
        CurrentScreen = Mapear(_flow.Current);
    }

    /// <summary>Tela Conflitos: abre a comparação do grupo escolhido.</summary>
    [RelayCommand]
    private void CompareConflict(ConflictGroup? group)
    {
        var grupo = group ?? Report?.RealConflicts.FirstOrDefault();

        EspelharGuardas();
        _flow.HasSelectedConflict = grupo is not null;
        _flow.Fire(FlowTrigger.CompareConflict); // exige item selecionado em Conflitos

        SelectedConflict = grupo;
        SelectedVersionToKeep = SuggestVersionToKeep(grupo);
        CurrentScreen = Mapear(_flow.Current);
    }

    /// <summary>Comparar: enfileira todas as versões exceto a que será mantida.
    /// Única operação sobre conteúdo do usuário: mover para quarentena (ADR-0002).
    /// Sem contexto válido o botão estaria desabilitado na UI; aqui vira no-op —
    /// a máquina jamais recebe este gatilho com fila vazia.</summary>
    [RelayCommand]
    private void QueueOtherVersionsForQuarantine()
    {
        if (SelectedConflict is null || SelectedVersionToKeep is null)
        {
            return;
        }

        foreach (var version in SelectedConflict.Versions)
        {
            if (!ReferenceEquals(version, SelectedVersionToKeep))
            {
                Quarantine.Queue(version.Path);
            }
        }

        EspelharGuardas();
        _flow.Fire(FlowTrigger.QueueOtherVersions); // exige fila não vazia
        CurrentScreen = Mapear(_flow.Current);
    }

    private bool HasQuarantineItems() => Quarantine.HasItems;

    /// <summary>Executa a movimentação. No esqueleto, o motor falso não toca em arquivo
    /// real: registra a fila na tela de confirmação e avança para a quarentena.</summary>
    [RelayCommand(CanExecute = nameof(HasQuarantineItems))]
    private void ConfirmQuarantine()
    {
        EspelharGuardas();
        _flow.Fire(FlowTrigger.ConfirmQuarantine); // exige fila não vazia

        Confirmation.Complete(Quarantine.Paths);
        CurrentScreen = Mapear(_flow.Current);
    }

    [RelayCommand(CanExecute = nameof(HasQuarantineItems))]
    private void OpenConfirmationFromQuarantine()
    {
        EspelharGuardas();
        _flow.Fire(FlowTrigger.OpenConfirmation);  // exige operation_id atribuído (§18)
        CurrentScreen = Mapear(_flow.Current);
    }

    [RelayCommand]
    private void Restart()
    {
        // Só parte da Confirmação; a máquina zera todas as guardas sozinha.
        _flow.Fire(FlowTrigger.Restart);

        // Reinício seguro: nenhuma análise contamina a seguinte.
        Quarantine.Clear();
        Confirmation.ItemsMoved = 0;
        Report = null;
        SelectedConflict = null;
        SelectedVersionToKeep = null;
        ScanProgressPercent = 0;
        CurrentScreen = Mapear(_flow.Current);     // Escolher pasta
    }

    /// <summary>
    /// Botão Voltar/"Repensar" — mapa definido NA máquina (ver FlowStateMachine):
    /// Duplicatas→Resumo; Conflitos→Duplicatas; Comparar→Conflitos;
    /// Escolher ação→Comparar ("Repensar": a escolha anterior deixa de valer —
    /// fila e seleção somem); Quarentena→Escolher ação preservando a fila.
    /// Bloqueado em Escolher pasta, Escaneando, Resumo e Confirmação: CanFire
    /// falso mantém a tela onde está (equivalente a botão desabilitado).
    /// </summary>
    [RelayCommand]
    private void GoBack()
    {
        EspelharGuardas();

        if (!_flow.CanFire(FlowTrigger.Back))
        {
            return;
        }

        var repensando = _flow.Current == FlowScreen.ChooseAction;
        _flow.Fire(FlowTrigger.Back);

        if (repensando)
        {
            // "Repensar": a ESCOLHA ENFILEIRADA deixa de valer — só a fila some.
            // O grupo em comparação permanece (a tela Comparar segue coerente) e
            // o usuário pode decidir de novo; limpar seleção aqui é papel do
            // Restart, que zera a análise inteira.
            Quarantine.Clear();
        }

        CurrentScreen = Mapear(_flow.Current);
    }
}
