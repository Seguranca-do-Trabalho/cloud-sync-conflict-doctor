using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Doctor.Gui.Engine;
using Doctor.Gui.Flow;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Minimum §15 SPEC flow: Choose Folder → Scan → Summary → Duplicates
/// → Real Conflicts → Compare → Choose Action → Quarantine → Confirmation.
///
/// G3 (t_8d08c08b): ALL navigation is DELEGATED to <see cref="FlowStateMachine"/>
/// (pure, no Avalonia). What remains here is presentation and orchestration: each
/// command mirrors the visible data into the machine's guards and fires the trigger —
/// no command changes screens on its own.
///
/// DOCUMENTED DECISION (throw vs. no-op):
/// - Advance trigger fired outside the valid order THROWS
///   InvalidTransitionException: a click that crosses the flow is a button-binding
///   bug, and silencing it would hide the error (correctness precedes UX in the SPEC).
/// - The Back button queries CanFire and STAYS where it is when the machine map
///   blocks (Choose Folder, Scanning, Summary and Confirmation):
///   equivalent to a disabled button — going back never surprises.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IScanEngine _engine;

    /// <summary>§15 flow state machine — sole authority for navigation.</summary>
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

    /// <summary>Compare screen: version the user decides to keep.</summary>
    [ObservableProperty]
    private ConflictVersion? _selectedVersionToKeep;

    // --- Sub-ViewModels per screen; only navigation and orchestration remain here ---

    /// <summary>Quarantine screen: queue of items marked for moving.</summary>
    public QuarantineViewModel Quarantine { get; } = new();

    /// <summary>Confirmation screen: record of the completed move.</summary>
    public ConfirmationViewModel Confirmation { get; } = new();

    public MainWindowViewModel(IScanEngine engine)
    {
        _engine = engine;

        // The confirm command is orchestration from MainWindow, but the state
        // (queue) belongs to the sub-VM: CanExecute follows its count.
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

    /// <summary>FlowScreen → Screen mapping for this VM (same nine values).</summary>
    private static Screen Map(FlowScreen screen) => screen switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, null),
    };

    /// <summary>
    /// Mirrors the visible analysis data into the machine's guards. Called at
    /// the start of EVERY navigation command: the machine always decides on the
    /// real analysis state, never on a stale flag.
    /// operation_id (§18): the fake id DERIVES from the queue content — with no
    /// queue there is no assignable id, so Confirmation requires a queue by construction.
    /// </summary>
    private void MirrorGuards()
    {
        _flow.HasReport = Report is not null;
        _flow.HasSelectedConflict = SelectedConflict is not null;
        _flow.HasQueuedItems = Quarantine.HasItems;
        _flow.HasOperationId = Quarantine.HasItems;
    }

    // --- "Choose Action" screen: §17 strategies as VISUAL PRESENTATION ---
    // FIXED list and ordered per docs/SPEC.md §17. Presentation only: the
    // per-strategy resolution logic belongs to EPIC 07 (Resolution Engine, scheduled).
    // EXTENSION POINT: when EPIC 07 delivers IResolutionStrategy, this screen
    // will receive options from the engine; the labels here are the visual contract.
    public static IReadOnlyList<string> ResolutionStrategies { get; } =
    [
        "Keep newest",
        "Keep largest",
        "Keep version from specific machine",
        "Choose manually",
    ];

    /// <summary>Mandatory §17 tiebreak rule, shown to the user.</summary>
    public static string TiebreakRule => "mtime → size → path";

    // --- "Confirmation" screen: deterministic fake manifest (§18) ---
    // Timestamp and id derived DETERMINISTICALLY (no local clock, no locale):
    // the skeleton uses the engine's fixed DEMO anchor. When EPIC 08 delivers the
    // real quarantine, the manifest will come from the engine — the labels remain.

    /// <summary>Fixed temporal anchor for the DEMO skeleton (never DateTime.Now).</summary>
    internal static DateTimeOffset DemoAnchor { get; } =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>§18 expected path: ConflictDoctor/quarantine/<timestamp>.</summary>
    public static string ExpectedQuarantinePath()
    {
        var t = DemoAnchor;
        return $"ConflictDoctor/quarantine/{t.Year:D4}-{t.Month:D2}-{t.Day:D2}T{t.Hour:D2}-{t.Minute:D2}";
    }

    /// <summary>Deterministic fake operation_id: stable hash of the queue content,
    /// in lowercase hex (locale-invariant), prefixed for readability.</summary>
    public string ExpectedOperationId
    {
        get
        {
            var content = string.Join("\n", Quarantine.Paths);
            var bytes = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(content));
            var hex = Convert.ToHexString(bytes).ToLowerInvariant();
            return $"op-{hex[..16]}";
        }
    }

    /// <summary>Item count displayed on Confirmation before enabling the final button.</summary>
    public int ConfirmationItemCount => Quarantine.Count;

    /// <summary>§18 expected path label for the Confirmation screen (x:Static).</summary>
    public static string ExpectedQuarantinePathLabel =>
        $"expected destination: {ExpectedQuarantinePath()}";

    // --- §15 Summary: the 5 questions on the first post-scan screen ---
    // Counts in raw digits (invariant); space with a fixed pt-BR decimal separator,
    // independent of the machine locale (determinism §3).

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

    // --- Per-screen visibility (avoids enum→bool converter in compiled XAML) ---

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

    /// <summary>Human-readable units; fixed dot decimal separator, invariant locale.</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => WithDot(bytes / 1_073_741_824.0) + " GB",
        >= 1_048_576 => WithDot(bytes / 1_048_576.0) + " MB",
        >= 1_024 => WithDot(bytes / 1_024.0) + " KB",
        _ => $"{bytes} B",
    };

    private static string WithDot(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Common path for simple commands: mirrors visible data into guards,
    /// fires the trigger on the machine, and publishes the resulting screen.
    /// Commands that need to adjust a guard between mirror and fire (Compare,
    /// queueing) or handle refusal (Back) do the steps manually.
    /// </summary>
    private void Navigate(FlowTrigger trigger)
    {
        MirrorGuards();
        _flow.Fire(trigger);
        CurrentScreen = Map(_flow.Current);
    }

    [RelayCommand]
    private void StartScan()
    {
        // Refused outside Choose Folder: restart only through the valid path (Restart).
        _flow.Fire(FlowTrigger.StartScan);

        // New scan starts from scratch — no residue from the previous run.
        Report = null;
        ScanProgressPercent = 0;
        Quarantine.Clear();
        Confirmation.ItemsMoved = 0;
        CurrentScreen = Map(_flow.Current);   // Scanning

        // Fake engine: synchronous execution; progress is simulated by the engine itself.
        Report = _engine.Scan(ChosenFolder, p => ScanProgressPercent = p);

        SelectedConflict = Report.RealConflicts.FirstOrDefault();
        SelectedVersionToKeep = SuggestVersionToKeep(SelectedConflict);

        MirrorGuards();
        _flow.Fire(FlowTrigger.ScanCompleted);   // only leaves Scanning with a complete report
        CurrentScreen = Map(_flow.Current);   // Summary
    }

    /// <summary>Deterministic suggestion (ADR-0003): mtime → size → path; never "first seen".
    /// Path compared in UTF-8 bytes, never locale.</summary>
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
        MirrorGuards();
        _flow.Fire(FlowTrigger.OpenDuplicates);  // requires report present
        CurrentScreen = Map(_flow.Current);
    }

    [RelayCommand]
    private void OpenConflicts()
    {
        MirrorGuards();
        _flow.Fire(FlowTrigger.OpenConflicts);   // requires report present
        CurrentScreen = Map(_flow.Current);
    }

    /// <summary>Conflicts screen: opens the comparison of the chosen group.</summary>
    [RelayCommand]
    private void CompareConflict(ConflictGroup? group)
    {
        var resolved = group ?? Report?.RealConflicts.FirstOrDefault();

        MirrorGuards();
        _flow.HasSelectedConflict = resolved is not null;
        _flow.Fire(FlowTrigger.CompareConflict); // requires selected item in Conflicts

        SelectedConflict = resolved;
        SelectedVersionToKeep = SuggestVersionToKeep(resolved);
        CurrentScreen = Map(_flow.Current);
    }

    /// <summary>Compare: queues all versions except the one to be kept.
    /// Only operation on user content: move to quarantine (ADR-0002).
    /// Without valid context the button would be disabled in the UI; here it becomes a no-op —
    /// the machine never receives this trigger with an empty queue.</summary>
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

        MirrorGuards();
        _flow.Fire(FlowTrigger.QueueOtherVersions); // requires non-empty queue
        CurrentScreen = Map(_flow.Current);
    }

    private bool HasQuarantineItems() => Quarantine.HasItems;

    /// <summary>Executes the move. In the skeleton, the fake engine does not touch
    /// any real file: it records the queue on the confirmation screen and advances to quarantine.</summary>
    [RelayCommand(CanExecute = nameof(HasQuarantineItems))]
    private void ConfirmQuarantine()
    {
        MirrorGuards();
        _flow.Fire(FlowTrigger.ConfirmQuarantine); // requires non-empty queue

        Confirmation.Complete(Quarantine.Paths);
        CurrentScreen = Map(_flow.Current);
    }

    [RelayCommand(CanExecute = nameof(HasQuarantineItems))]
    private void OpenConfirmationFromQuarantine()
    {
        MirrorGuards();
        _flow.Fire(FlowTrigger.OpenConfirmation);  // requires operation_id assigned (§18)
        CurrentScreen = Map(_flow.Current);
    }

    [RelayCommand]
    private void Restart()
    {
        // Only from Confirmation; the machine resets all guards on its own.
        _flow.Fire(FlowTrigger.Restart);

        // Safe restart: no scan contaminates the next one.
        Quarantine.Clear();
        Confirmation.ItemsMoved = 0;
        Report = null;
        SelectedConflict = null;
        SelectedVersionToKeep = null;
        ScanProgressPercent = 0;
        CurrentScreen = Map(_flow.Current);     // Choose Folder
    }

    /// <summary>
    /// Back/"Rethink" button — map defined IN the machine (see FlowStateMachine):
    /// Duplicates→Summary; Conflicts→Duplicates; Compare→Conflicts;
    /// Choose Action→Compare ("Rethink": the previous choice becomes invalid —
    /// queue and selection disappear); Quarantine→Choose Action preserving the queue.
    /// Blocked at Choose Folder, Scanning, Summary and Confirmation: CanFire
    /// false keeps the screen where it is (equivalent to a disabled button).
    /// </summary>
    [RelayCommand]
    private void GoBack()
    {
        MirrorGuards();

        if (!_flow.CanFire(FlowTrigger.Back))
        {
            return;
        }

        var rethinking = _flow.Current == FlowScreen.ChooseAction;
        _flow.Fire(FlowTrigger.Back);

        if (rethinking)
        {
            // "Rethink": the QUEUED CHOICE becomes invalid — only the queue disappears.
            // The group under comparison remains (the Compare screen stays consistent) and
            // the user can decide again; clearing selection here is the job of
            // Restart, which resets the entire scan.
            Quarantine.Clear();
        }

        CurrentScreen = Map(_flow.Current);
    }
}
