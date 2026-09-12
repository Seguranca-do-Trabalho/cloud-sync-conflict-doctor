namespace Doctor.Gui.Flow;

/// <summary>
/// The 9 screens of the §15 SPEC flow, in canonical navigation order.
/// Pure type (no Avalonia): the state machine is testable independently of the UI.
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
/// Navigation triggers — one per GUI button/action command.
/// Names mirror MainWindowViewModel commands so the command → trigger
/// mapping is direct and auditable.
/// </summary>
public enum FlowTrigger
{
    /// <summary>"Scan folder" on the Choose Folder screen.</summary>
    StartScan,

    /// <summary>Scan completion: only fires with a complete report.</summary>
    ScanCompleted,

    /// <summary>"View duplicates" on Summary.</summary>
    OpenDuplicates,

    /// <summary>"View real conflicts" / "Continue".</summary>
    OpenConflicts,

    /// <summary>"Compare versions" on the Conflicts screen.</summary>
    CompareConflict,

    /// <summary>"Move other versions to quarantine" on Compare.</summary>
    QueueOtherVersions,

    /// <summary>"Move to quarantine now" on Choose Action.</summary>
    ConfirmQuarantine,

    /// <summary>"Record move..." on Quarantine.</summary>
    OpenConfirmation,

    /// <summary>"Browse another folder" (new scan) on Confirmation.</summary>
    Restart,

    /// <summary>"Back"/"Rethink" buttons — allowed only where it won't expose inconsistent state.</summary>
    Back,
}

/// <summary>
/// Thrown when a trigger is fired outside the valid §15 flow order
/// or without the guard precondition. Deliberate fail-fast (documented decision
/// in card t_8d08c08b): silencing it would hide a button-binding error;
/// correctness precedes UX in SPEC priorities.
/// </summary>
public class InvalidTransitionException : InvalidOperationException
{
    public InvalidTransitionException(FlowScreen origin, FlowTrigger trigger)
        : base($"Invalid transition: trigger {trigger} is not allowed in state {origin} " +
               "(§15 flow; use CanFire to query the transition without throwing).")
    {
        Origin = origin;
        Trigger = trigger;
    }

    public FlowScreen Origin { get; }

    public FlowTrigger Trigger { get; }
}
