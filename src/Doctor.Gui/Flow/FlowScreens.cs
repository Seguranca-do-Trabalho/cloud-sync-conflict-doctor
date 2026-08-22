namespace Doctor.Gui.Flow;

/// <summary>
/// As 9 telas do fluxo §15 da SPEC, na ordem canônica de navegação.
/// Tipo puro (sem Avalonia): a máquina de estados é testável isolada da UI.
/// </summary>
public enum FlowScreen
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

/// <summary>
/// Gatilhos de navegação — um por comando de botão/ação da GUI.
/// Nomes espelham os comandos da MainWindowViewModel para a correspondência
/// comando → gatilho ser direta e auditável.
/// </summary>
public enum FlowTrigger
{
    /// <summary>"Escanear pasta" na tela Escolher pasta.</summary>
    StartScan,

    /// <summary>Conclusão do scan: só dispara com relatório completo.</summary>
    ScanCompleted,

    /// <summary>"Ver duplicatas" no Resumo.</summary>
    OpenDuplicates,

    /// <summary>"Ver conflitos reais" / "Continuar".</summary>
    OpenConflicts,

    /// <summary>"Comparar versões" na tela Conflitos.</summary>
    CompareConflict,

    /// <summary>"Mover para quarentena as outras versões" em Comparar.</summary>
    QueueOtherVersions,

    /// <summary>"Mover para quarentena agora" em Escolher ação.</summary>
    ConfirmQuarantine,

    /// <summary>"Registrar movimentação..." na Quarentena.</summary>
    OpenConfirmation,

    /// <summary>"Examinar outra pasta" (nova análise) na Confirmação.</summary>
    Restart,

    /// <summary>Botões "Voltar"/"Repensar" — permitido apenas onde não expõe estado inconsistente.</summary>
    Back,
}

/// <summary>
/// Lançada quando um gatilho é disparado fora da ordem válida do fluxo §15
/// ou sem a pré-condição da guarda. Fail-fast deliberado (decisão documentada
/// no card t_8d08c08b): silenciar esconderia erro de ligação de botão;
/// corretidade precede UX nas prioridades da SPEC.
/// </summary>
public class TransicaoInvalidaException : InvalidOperationException
{
    public TransicaoInvalidaException(FlowScreen origem, FlowTrigger gatilho)
        : base($"Transição inválida: gatilho {gatilho} não é permitido no estado {origem} " +
               "(fluxo §15; use CanFire para consultar a transição sem lançar).")
    {
        Origem = origem;
        Gatilho = gatilho;
    }

    public FlowScreen Origem { get; }

    public FlowTrigger Gatilho { get; }
}
