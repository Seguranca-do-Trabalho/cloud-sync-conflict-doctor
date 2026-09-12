using Doctor.Gui.Flow;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G3 (t_8d08c08b) — §15 flow state machine, pure (no Avalonia).
/// Cycle 1 (RED): the 9 states are the 9 screens; only the valid order exists;
/// any trigger outside the order throws InvalidTransitionException and the current
/// state does NOT change (fail-fast: silencing would hide a wrong button binding).
/// </summary>
public class FlowStateMachineTests
{
    private static FlowStateMachine NewMachine() => new();

    // ------------------------------------------------------------------
    // Complete happy path: the only valid §15 flow sequence.
    // ------------------------------------------------------------------
    [Fact]
    public void Valid_path_traverses_all_nine_screens_in_flow_order()
    {
        var m = NewMachine();
        var order = new List<FlowScreen> { m.Current };

        m.Fire(FlowTrigger.StartScan);
        order.Add(m.Current);

        m.HasReport = true;                 // complete report arrived
        m.Fire(FlowTrigger.ScanCompleted);
        order.Add(m.Current);

        m.Fire(FlowTrigger.OpenDuplicates);
        order.Add(m.Current);

        m.Fire(FlowTrigger.Back);
        order.Add(m.Current);

        m.Fire(FlowTrigger.OpenConflicts);
        order.Add(m.Current);

        m.HasSelectedConflict = true;       // group chosen on Conflicts screen
        m.Fire(FlowTrigger.CompareConflict);
        order.Add(m.Current);

        m.HasQueuedItems = true;            // unkept versions queued
        m.Fire(FlowTrigger.QueueOtherVersions);
        order.Add(m.Current);

        m.Fire(FlowTrigger.ConfirmQuarantine);
        order.Add(m.Current);

        m.HasOperationId = true;            // fake operation_id assigned (§18)
        m.Fire(FlowTrigger.OpenConfirmation);
        order.Add(m.Current);

        var expected = new[]
        {
            FlowScreen.ChooseFolder, FlowScreen.Scanning, FlowScreen.Summary,
            FlowScreen.Duplicates, FlowScreen.Summary, FlowScreen.Conflicts,
            FlowScreen.Compare, FlowScreen.ChooseAction, FlowScreen.Quarantine,
            FlowScreen.Confirmation,
        };

        Assert.Equal(expected, order);
    }

    // ------------------------------------------------------------------
    // Parametrized matrix of ALL valid graph edges.
    // (origin, trigger, destination, preconditions to enable)
    // ------------------------------------------------------------------
    public static TheoryData<FlowScreen, FlowTrigger, FlowScreen, string> ValidEdges() => new()
    {
        // ChooseFolder → Scanning
        { FlowScreen.ChooseFolder, FlowTrigger.StartScan, FlowScreen.Scanning, "" },
        // Scanning → Summary (only with complete report)
        { FlowScreen.Scanning, FlowTrigger.ScanCompleted, FlowScreen.Summary, "report" },
        // Summary opens Duplicates or Conflicts (with report)
        { FlowScreen.Summary, FlowTrigger.OpenDuplicates, FlowScreen.Duplicates, "report" },
        { FlowScreen.Summary, FlowTrigger.OpenConflicts, FlowScreen.Conflicts, "report" },
        // Duplicates → Conflicts (Continue) or back to Summary
        { FlowScreen.Duplicates, FlowTrigger.OpenConflicts, FlowScreen.Conflicts, "report" },
        { FlowScreen.Duplicates, FlowTrigger.Back, FlowScreen.Summary, "report" },
        // Conflicts → Compare (with selection) or back to Duplicates
        { FlowScreen.Conflicts, FlowTrigger.CompareConflict, FlowScreen.Compare, "report,selection" },
        { FlowScreen.Conflicts, FlowTrigger.Back, FlowScreen.Duplicates, "report" },
        // Compare → Choose Action (with queue) or back to Conflicts
        { FlowScreen.Compare, FlowTrigger.QueueOtherVersions, FlowScreen.ChooseAction, "report,selection,queue" },
        { FlowScreen.Compare, FlowTrigger.Back, FlowScreen.Conflicts, "report,selection" },
        // Choose Action → Quarantine (non-empty queue) or Rethink → Compare
        { FlowScreen.ChooseAction, FlowTrigger.ConfirmQuarantine, FlowScreen.Quarantine, "report,selection,queue" },
        { FlowScreen.ChooseAction, FlowTrigger.Back, FlowScreen.Compare, "report,selection,queue" },
        // Quarantine → Confirmation (with operation_id) or back to Choose Action
        { FlowScreen.Quarantine, FlowTrigger.OpenConfirmation, FlowScreen.Confirmation, "report,selection,queue,opid" },
        { FlowScreen.Quarantine, FlowTrigger.Back, FlowScreen.ChooseAction, "report,selection,queue" },
        // Confirmation → new scan
        { FlowScreen.Confirmation, FlowTrigger.Restart, FlowScreen.ChooseFolder, "report,selection,queue,opid" },
    };

    [Theory]
    [MemberData(nameof(ValidEdges))]
    public void Valid_edge_leads_to_exact_destination(
        FlowScreen origin, FlowTrigger trigger, FlowScreen destination, string preconditions)
    {
        var m = MachineAt(origin, preconditions);

        m.Fire(trigger);

        Assert.Equal(destination, m.Current);
    }

    // ------------------------------------------------------------------
    // Invalid transition: throws and keeps the state (no click sequence
    // leads to a state outside the valid order).
    // ------------------------------------------------------------------
    public static TheoryData<FlowScreen, FlowTrigger, string> InvalidEdges() => new()
    {
        // Forbidden shortcuts from the start
        { FlowScreen.ChooseFolder, FlowTrigger.ScanCompleted, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.OpenDuplicates, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.OpenConflicts, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.CompareConflict, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.ConfirmQuarantine, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.OpenConfirmation, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.Back, "" },
        // Scanning: nothing leaves without a complete report (no back, no restart)
        { FlowScreen.Scanning, FlowTrigger.Back, "" },
        { FlowScreen.Scanning, FlowTrigger.Restart, "" },
        { FlowScreen.Scanning, FlowTrigger.OpenDuplicates, "" },
        { FlowScreen.Scanning, FlowTrigger.ScanCompleted, "" }, // no "report" precondition
        // Summary: without report no one enters Duplicates/Conflicts; no shortcuts
        { FlowScreen.Summary, FlowTrigger.OpenDuplicates, "" },
        { FlowScreen.Summary, FlowTrigger.OpenConflicts, "" },
        { FlowScreen.Summary, FlowTrigger.ConfirmQuarantine, "" },
        { FlowScreen.Summary, FlowTrigger.Back, "" },
        { FlowScreen.Summary, FlowTrigger.StartScan, "" },
        // Duplicates: without report cannot enter or advance; no queue shortcuts
        { FlowScreen.Duplicates, FlowTrigger.ConfirmQuarantine, "report" },
        { FlowScreen.Duplicates, FlowTrigger.QueueOtherVersions, "report" },
        { FlowScreen.Duplicates, FlowTrigger.OpenDuplicates, "report" },
        // Conflicts: without selection cannot compare; without report cannot enter conflicts again
        { FlowScreen.Conflicts, FlowTrigger.CompareConflict, "report" },
        { FlowScreen.Conflicts, FlowTrigger.ConfirmQuarantine, "report,selection" },
        // Compare: without selection cannot queue; without queue cannot confirm
        { FlowScreen.Compare, FlowTrigger.QueueOtherVersions, "report,selection" },
        { FlowScreen.Compare, FlowTrigger.ConfirmQuarantine, "report,selection" },
        // Choose Action: empty queue does not enter Quarantine
        { FlowScreen.ChooseAction, FlowTrigger.ConfirmQuarantine, "report,selection" },
        { FlowScreen.ChooseAction, FlowTrigger.OpenConfirmation, "report,selection,queue" },
        // Quarantine: without operation_id cannot see Confirmation
        { FlowScreen.Quarantine, FlowTrigger.OpenConfirmation, "report,selection,queue" },
        { FlowScreen.Quarantine, FlowTrigger.QueueOtherVersions, "report,selection,queue" },
        // Confirmation: recorded operation does not go back or re-queue
        { FlowScreen.Confirmation, FlowTrigger.Back, "report,selection,queue,opid" },
        { FlowScreen.Confirmation, FlowTrigger.ConfirmQuarantine, "report,selection,queue,opid" },
        { FlowScreen.Confirmation, FlowTrigger.OpenDuplicates, "report,selection,queue,opid" },
        { FlowScreen.Confirmation, FlowTrigger.StartScan, "report,selection,queue,opid" },
    };

    [Theory]
    [MemberData(nameof(InvalidEdges))]
    public void Invalid_transition_throws_and_keeps_state(
        FlowScreen origin, FlowTrigger trigger, string preconditions)
    {
        var m = MachineAt(origin, preconditions);

        var exception = Assert.Throws<InvalidTransitionException>(() => m.Fire(trigger));

        Assert.Equal(origin, m.Current);
        Assert.Contains(trigger.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(origin.ToString(), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>CanFire mirrors Fire: true only where Fire would not throw.</summary>
    [Theory]
    [MemberData(nameof(ValidEdges))]
    public void CanFire_mirrors_valid_edges(
        FlowScreen origin, FlowTrigger trigger, FlowScreen _, string preconditions)
    {
        var m = MachineAt(origin, preconditions);

        Assert.True(m.CanFire(trigger));
    }

    [Theory]
    [MemberData(nameof(InvalidEdges))]
    public void CanFire_rejects_every_invalid_edge(
        FlowScreen origin, FlowTrigger trigger, string preconditions)
    {
        var m = MachineAt(origin, preconditions);

        Assert.False(m.CanFire(trigger));
    }

    // ------------------------------------------------------------------
    // Helper: positions the machine at a state with preconditions enabled.
    // Preconditions are always reached via the valid path from
    // ChooseFolder — never injected by reflection — except the guard
    // flags, which represent world data (report, selection, queue, id).
    // ------------------------------------------------------------------
    private static FlowStateMachine MachineAt(FlowScreen origin, string preconditions)
    {
        var m = NewMachine();
        var wanted = preconditions.Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (origin == FlowScreen.ChooseFolder)
        {
            return m;                           // initial position: nothing crossed
        }

        // 1) Valid path to the origin. Guard flags enabled along the
        //    path exist only to CROSS each intermediate transition —
        //    they are normalized at the end, never leaking to the trigger under test.
        m.Fire(FlowTrigger.StartScan);                        // → Scanning

        if (origin != FlowScreen.Scanning)
        {
            m.HasReport = true;
            m.Fire(FlowTrigger.ScanCompleted);                // → Summary

            if (origin != FlowScreen.Summary)
            {
                m.Fire(FlowTrigger.OpenDuplicates);           // → Duplicates

                if (origin != FlowScreen.Duplicates)
                {
                    m.Fire(FlowTrigger.OpenConflicts);        // → Conflicts

                    if (origin != FlowScreen.Conflicts)
                    {
                        m.HasSelectedConflict = true;
                        m.Fire(FlowTrigger.CompareConflict);  // → Compare

                        if (origin != FlowScreen.Compare)
                        {
                            m.HasQueuedItems = true;
                            m.Fire(FlowTrigger.QueueOtherVersions); // → ChooseAction

                            if (origin != FlowScreen.ChooseAction)
                            {
                                m.Fire(FlowTrigger.ConfirmQuarantine); // → Quarantine

                                if (origin != FlowScreen.Quarantine)
                                {
                                    m.HasOperationId = true;
                                    m.Fire(FlowTrigger.OpenConfirmation); // → Confirmation
                                }
                            }
                        }
                    }
                }
            }
        }

        // 2) Normalize world data to EXACTLY the requested preconditions:
        //    the scenario describes the visible state at the moment of the
        //    click under test, nothing more. This is what makes each negative
        //    guard fail for the right reason (missing flag), not wrong machine position.
        m.HasReport = wanted.Contains("report");
        m.HasSelectedConflict = wanted.Contains("selection");
        m.HasQueuedItems = wanted.Contains("queue");
        m.HasOperationId = wanted.Contains("opid");

        return m;
    }
}
