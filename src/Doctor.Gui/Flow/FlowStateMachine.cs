using Doctor.Gui.Flow;

namespace Doctor.Gui.Flow;

/// <summary>
/// Máquina de estados do fluxo §15 da SPEC — pura, sem Avalonia, determinística.
///
/// GRAFO DE TRANSIÇÕES (origem → destino, pelo gatilho):
///
///   ChooseFolder  --StartScan--------------------------→ Scanning
///   Scanning      --ScanCompleted [report]------------→ Summary
///   Summary       --OpenDuplicates [report]-----------→ Duplicates
///   Summary       --OpenConflicts  [report]-----------→ Conflicts
///   Duplicates    --OpenConflicts  [report]-----------→ Conflicts
///   Duplicates    --Back          [report]------------→ Summary
///   Conflicts     --CompareConflict[report+selecao]--→ Compare
///   Conflicts     --Back          [report]------------→ Duplicates
///   Compare       --QueueOtherVersions[fila]---------→ ChooseAction
///   Compare       --Back          [report+selecao]----→ Conflicts
///   ChooseAction  --ConfirmQuarantine[fila]----------→ Quarantine
///   ChooseAction  --Back          [fila]--------------→ Compare
///   Quarantine    --OpenConfirmation[opid]-----------→ Confirmation
///   Quarantine    --Back          [fila]--------------→ ChooseAction
///   Confirmation  --Restart------------------------------→ ChooseFolder
///
/// GUARDAS (pré-condições por transição):
///   [report]        — só sai de Scanning com relatório completo; Duplicatas e
///                     Conflitos exigem relatório presente;
///   [selecao]       — não entra em Comparar sem item selecionado em Conflitos;
///   [fila]          — não entra em Escolher ação/Quarentena com fila vazia;
///   [opid]          — não entra em Confirmação sem operation_id atribuído (§18).
///
/// MAPA DO BOTÃO VOLTAR (documentado; "Repensar" é o Back de ChooseAction):
///   Duplicatas → Resumo; Conflitos → Duplicatas; Comparar → Conflitos;
///   Escolher ação → Comparar (limpa a fila: a escolha anterior deixa de valer);
///   Quarentena → Escolher ação. BLOQUEADO em Escolher pasta (não há para onde
///   voltar), Escaneando (abortaria scan sem relatório), Resumo (voltaria ao
///   início sem reinício limpo) e Confirmação (operação já registrada — voltar
///   exporia estado inconsistente pós-operação).
///
/// Transição inválida LANÇA TransicaoInvalidaException e mantém o estado
/// (fail-fast); CanFire consulta a mesma tabela sem lançar, para a UI
/// desabilitar botões. A MainWindowViewModel delega toda navegação aqui:
/// nenhuma sequência de cliques leva a um estado fora da ordem válida.
/// </summary>
public sealed class FlowStateMachine
{
    /// <summary>Estado mutável interno; encapsulado para o grafo estático não vazar.</summary>
    private sealed class ScreenState
    {
        public FlowScreen Screen { get; set; } = FlowScreen.ChooseFolder;
        public bool HasReport { get; set; }
        public bool HasSelectedConflict { get; set; }
        public bool HasQueuedItems { get; set; }
        public bool HasOperationId { get; set; }
    }

    private readonly record struct Transition(FlowScreen Destino, Func<ScreenState, bool> Guarda);

    private static readonly IReadOnlyDictionary<(FlowScreen Origem, FlowTrigger Gatilho), Transition> Grafo =
        new Dictionary<(FlowScreen, FlowTrigger), Transition>
        {
            [(FlowScreen.ChooseFolder, FlowTrigger.StartScan)] = new(FlowScreen.Scanning, Sempre),

            [(FlowScreen.Scanning, FlowTrigger.ScanCompleted)] =
                new(FlowScreen.Summary, ExigeRelatorio),

            [(FlowScreen.Summary, FlowTrigger.OpenDuplicates)] =
                new(FlowScreen.Duplicates, ExigeRelatorio),
            [(FlowScreen.Summary, FlowTrigger.OpenConflicts)] =
                new(FlowScreen.Conflicts, ExigeRelatorio),

            [(FlowScreen.Duplicates, FlowTrigger.OpenConflicts)] =
                new(FlowScreen.Conflicts, ExigeRelatorio),
            [(FlowScreen.Duplicates, FlowTrigger.Back)] =
                new(FlowScreen.Summary, ExigeRelatorio),

            [(FlowScreen.Conflicts, FlowTrigger.CompareConflict)] =
                new(FlowScreen.Compare, s => s.HasReport && s.HasSelectedConflict),
            [(FlowScreen.Conflicts, FlowTrigger.Back)] =
                new(FlowScreen.Duplicates, ExigeRelatorio),

            [(FlowScreen.Compare, FlowTrigger.QueueOtherVersions)] =
                new(FlowScreen.ChooseAction, ExigeFila),
            [(FlowScreen.Compare, FlowTrigger.Back)] =
                new(FlowScreen.Conflicts, s => s.HasReport && s.HasSelectedConflict),

            [(FlowScreen.ChooseAction, FlowTrigger.ConfirmQuarantine)] =
                new(FlowScreen.Quarantine, ExigeFila),
            [(FlowScreen.ChooseAction, FlowTrigger.Back)] = new(FlowScreen.Compare, ExigeFila),

            [(FlowScreen.Quarantine, FlowTrigger.OpenConfirmation)] =
                new(FlowScreen.Confirmation, s => s.HasOperationId),
            [(FlowScreen.Quarantine, FlowTrigger.Back)] = new(FlowScreen.ChooseAction, ExigeFila),

            [(FlowScreen.Confirmation, FlowTrigger.Restart)] =
                new(FlowScreen.ChooseFolder, Sempre),
        };

    private readonly ScreenState _estado = new();

    /// <summary>Tela corrente.</summary>
    public FlowScreen Current => _estado.Screen;

    // --- Guardas: dados do mundo que a GUI liga antes de disparar o gatilho ---

    /// <summary>Relatório completo disponível (scan concluído).</summary>
    public bool HasReport { get => _estado.HasReport; set => _estado.HasReport = value; }

    /// <summary>Há grupo selecionado em Conflitos (pré-condição de Comparar).</summary>
    public bool HasSelectedConflict
    {
        get => _estado.HasSelectedConflict;
        set => _estado.HasSelectedConflict = value;
    }

    /// <summary>Fila da quarentena não vazia.</summary>
    public bool HasQueuedItems
    {
        get => _estado.HasQueuedItems;
        set => _estado.HasQueuedItems = value;
    }

    /// <summary>operation_id fake atribuído à operação (§18).</summary>
    public bool HasOperationId
    {
        get => _estado.HasOperationId;
        set => _estado.HasOperationId = value;
    }

    /// <summary>
    /// Consulta se o gatilho é válido no estado corrente, sem lançar e sem mudar estado.
    /// A UI usa isto para habilitar/desabilitar botões.
    /// </summary>
    public bool CanFire(FlowTrigger gatilho) =>
        Grafo.TryGetValue((_estado.Screen, gatilho), out var transicao)
        && transicao.Guarda(_estado);

    /// <summary>
    /// Dispara o gatilho. Transição inexistente ou com guarda falsa lança
    /// TransicaoInvalidaException e mantém o estado corrente.
    /// </summary>
    public void Fire(FlowTrigger gatilho)
    {
        if (!Grafo.TryGetValue((_estado.Screen, gatilho), out var transicao)
            || !transicao.Guarda(_estado))
        {
            throw new TransicaoInvalidaException(_estado.Screen, gatilho);
        }

        if (gatilho == FlowTrigger.Back && transicao.Destino == FlowScreen.Compare)
        {
            // "Repensar": a escolha anterior deixa de valer — a fila esvazia.
            _estado.HasQueuedItems = false;
        }

        if (gatilho == FlowTrigger.Restart)
        {
            Reset();
            return;
        }

        _estado.Screen = transicao.Destino;
    }

    /// <summary>
    /// Reinício seguro: volta a Escolher pasta zerando TODAS as guardas — nenhum
    /// resíduo de uma análise vaza para a seguinte (provado por teste no card).
    /// </summary>
    public void Reset()
    {
        _estado.Screen = FlowScreen.ChooseFolder;
        _estado.HasReport = false;
        _estado.HasSelectedConflict = false;
        _estado.HasQueuedItems = false;
        _estado.HasOperationId = false;
    }

    private static bool Sempre(ScreenState _) => true;

    private static bool ExigeRelatorio(ScreenState s) => s.HasReport;

    private static bool ExigeFila(ScreenState s) =>
        s.HasReport && s.HasSelectedConflict && s.HasQueuedItems;
}
