using Doctor.Gui.Flow;

namespace Doctor.Gui.Flow;

/// <summary>
/// §15 SPEC flow state machine — pure, no Avalonia, deterministic.
///
/// TRANSITION GRAPH (origin → destination, by trigger):
///
///   ChooseFolder  --StartScan--------------------------→ Scanning
///   Scanning      --ScanCompleted [report]------------→ Summary
///   Summary       --OpenDuplicates [report]-----------→ Duplicates
///   Summary       --OpenConflicts  [report]-----------→ Conflicts
///   Duplicates    --OpenConflicts  [report]-----------→ Conflicts
///   Duplicates    --Back          [report]------------→ Summary
///   Conflicts     --CompareConflict[report+selection]--→ Compare
///   Conflicts     --Back          [report]------------→ Duplicates
///   Compare       --QueueOtherVersions[queue]---------→ ChooseAction
///   Compare       --Back          [report+selection]--→ Conflicts
///   ChooseAction  --ConfirmQuarantine[queue]----------→ Quarantine
///   ChooseAction  --Back          [queue]-------------→ Compare
///   Quarantine    --OpenConfirmation[opid]-----------→ Confirmation
///   Quarantine    --Back          [queue]-------------→ ChooseAction
///   Confirmation  --Restart------------------------------→ ChooseFolder
///
/// GUARDS (preconditions per transition):
///   [report]        — only leaves Scanning with a complete report; Duplicates and
///                     Conflicts require a report to be present;
///   [selection]     — does not enter Compare without a selected item in Conflicts;
///   [queue]         — does not enter Choose Action/Quarantine with an empty queue;
///   [opid]          — does not enter Confirmation without an assigned operation_id (§18).
///
/// BACK BUTTON MAP (documented; "Rethink" is the Back from ChooseAction):
///   Duplicates → Summary; Conflicts → Duplicates; Compare → Conflicts;
///   Choose Action → Compare (clears the queue: the previous choice becomes invalid);
///   Quarantine → Choose Action. BLOCKED at Choose Folder (nowhere to go
///   back to), Scanning (would abort scan without a report), Summary (would go
///   back to the start without a clean restart) and Confirmation (operation already
///   recorded — going back would expose inconsistent post-operation state).
///
/// Invalid transition THROWS InvalidTransitionException and keeps the state
/// (fail-fast); CanFire queries the same table without throwing, so the UI
/// can disable buttons. MainWindowViewModel delegates all navigation here:
/// no sequence of clicks leads to a state outside the valid order.
/// </summary>
public sealed class FlowStateMachine
{
    /// <summary>Mutable internal state; encapsulated so the static graph cannot leak.</summary>
    private sealed class ScreenState
    {
        public FlowScreen Screen { get; set; } = FlowScreen.ChooseFolder;
        public bool HasReport { get; set; }
        public bool HasSelectedConflict { get; set; }
        public bool HasQueuedItems { get; set; }
        public bool HasOperationId { get; set; }
    }

    private readonly record struct Transition(FlowScreen Destination, Func<ScreenState, bool> Guard);

    private static readonly IReadOnlyDictionary<(FlowScreen Origin, FlowTrigger Trigger), Transition> Graph =
        new Dictionary<(FlowScreen, FlowTrigger), Transition>
        {
            [(FlowScreen.ChooseFolder, FlowTrigger.StartScan)] = new(FlowScreen.Scanning, Always),

            [(FlowScreen.Scanning, FlowTrigger.ScanCompleted)] =
                new(FlowScreen.Summary, RequiresReport),

            [(FlowScreen.Summary, FlowTrigger.OpenDuplicates)] =
                new(FlowScreen.Duplicates, RequiresReport),
            [(FlowScreen.Summary, FlowTrigger.OpenConflicts)] =
                new(FlowScreen.Conflicts, RequiresReport),

            [(FlowScreen.Duplicates, FlowTrigger.OpenConflicts)] =
                new(FlowScreen.Conflicts, RequiresReport),
            [(FlowScreen.Duplicates, FlowTrigger.Back)] =
                new(FlowScreen.Summary, RequiresReport),

            [(FlowScreen.Conflicts, FlowTrigger.CompareConflict)] =
                new(FlowScreen.Compare, s => s.HasReport && s.HasSelectedConflict),
            [(FlowScreen.Conflicts, FlowTrigger.Back)] =
                new(FlowScreen.Duplicates, RequiresReport),

            [(FlowScreen.Compare, FlowTrigger.QueueOtherVersions)] =
                new(FlowScreen.ChooseAction, RequiresQueue),
            [(FlowScreen.Compare, FlowTrigger.Back)] =
                new(FlowScreen.Conflicts, s => s.HasReport && s.HasSelectedConflict),

            [(FlowScreen.ChooseAction, FlowTrigger.ConfirmQuarantine)] =
                new(FlowScreen.Quarantine, RequiresQueue),
            [(FlowScreen.ChooseAction, FlowTrigger.Back)] = new(FlowScreen.Compare, RequiresQueue),

            [(FlowScreen.Quarantine, FlowTrigger.OpenConfirmation)] =
                new(FlowScreen.Confirmation, s => s.HasOperationId),
            [(FlowScreen.Quarantine, FlowTrigger.Back)] = new(FlowScreen.ChooseAction, RequiresQueue),

            [(FlowScreen.Confirmation, FlowTrigger.Restart)] =
                new(FlowScreen.ChooseFolder, Always),
        };

    private readonly ScreenState _state = new();

    /// <summary>Current screen.</summary>
    public FlowScreen Current => _state.Screen;

    // --- Guards: world data the GUI sets before firing a trigger ---

    /// <summary>Complete report available (scan finished).</summary>
    public bool HasReport { get => _state.HasReport; set => _state.HasReport = value; }

    /// <summary>Conflict group selected in Conflicts (precondition for Compare).</summary>
    public bool HasSelectedConflict
    {
        get => _state.HasSelectedConflict;
        set => _state.HasSelectedConflict = value;
    }

    /// <summary>Non-empty quarantine queue.</summary>
    public bool HasQueuedItems
    {
        get => _state.HasQueuedItems;
        set => _state.HasQueuedItems = value;
    }

    /// <summary>Fake operation_id assigned to the operation (§18).</summary>
    public bool HasOperationId
    {
        get => _state.HasOperationId;
        set => _state.HasOperationId = value;
    }

    /// <summary>
    /// Queries whether the trigger is valid in the current state, without throwing
    /// or changing state. The UI uses this to enable/disable buttons.
    /// </summary>
    public bool CanFire(FlowTrigger trigger) =>
        Graph.TryGetValue((_state.Screen, trigger), out var transition)
        && transition.Guard(_state);

    /// <summary>
    /// Fires the trigger. Nonexistent transition or false guard throws
    /// InvalidTransitionException and keeps the current state.
    /// </summary>
    public void Fire(FlowTrigger trigger)
    {
        if (!Graph.TryGetValue((_state.Screen, trigger), out var transition)
            || !transition.Guard(_state))
        {
            throw new InvalidTransitionException(_state.Screen, trigger);
        }

        if (trigger == FlowTrigger.Back && transition.Destination == FlowScreen.Compare)
        {
            // "Rethink": the previous choice becomes invalid — the queue empties.
            _state.HasQueuedItems = false;
        }

        if (trigger == FlowTrigger.Restart)
        {
            Reset();
            return;
        }

        _state.Screen = transition.Destination;
    }

    /// <summary>
    /// Safe restart: returns to Choose Folder resetting ALL guards — no
    /// residue from one scan leaks into the next (proven by test in the card).
    /// </summary>
    public void Reset()
    {
        _state.Screen = FlowScreen.ChooseFolder;
        _state.HasReport = false;
        _state.HasSelectedConflict = false;
        _state.HasQueuedItems = false;
        _state.HasOperationId = false;
    }

    private static bool Always(ScreenState _) => true;

    private static bool RequiresReport(ScreenState s) => s.HasReport;

    private static bool RequiresQueue(ScreenState s) =>
        s.HasReport && s.HasSelectedConflict && s.HasQueuedItems;
}
