namespace Doctor.Core;

/// <summary>
/// T14 — Resolution engine (SPEC §17; ADR-0003 rule 2). Produces ONLY THE PLAN
/// (<see cref="ResolutionPlan"/>) for each ScanPipeline group: winner and list
/// of sacrifices, both deterministic. No file operation happens here
/// (moving/quarantine is a downstream phase — SPEC §18, ADR-0002).
///
/// Mandatory tie-breaking in ANY decision between distinct entries:
/// mtime (newest wins) → size (largest wins) → path ascending byte-wise
/// (<see cref="string.CompareOrdinal"/>). Never first-seen, never arrival order,
/// never filesystem order (SPEC §17 "Ties"; ADR-0003).
///
/// Fail-closed (SPEC §2.1): invalid entry (empty/null group, manual choice
/// outside the group, machineId without representative) throws — does not return
/// a plausible plan.
/// </summary>
public static class Resolution
{
    /// <summary>
    /// Resolution plan for a group: <paramref name="winner"/> stays in place,
    /// <paramref name="sacrifices"/> go to downstream quarantine (future phase,
    /// outside this card). Sacrifices always in canonical byte-wise path order.
    /// </summary>
    public sealed record ResolutionPlan(
        ConflictGroup Group,
        ResolutionStrategy Strategy,
        FileEntry Winner,
        IReadOnlyList<Sacrifice> Sacrifices);

    /// <summary>Member destined for quarantine by the plan, with auditable reason.</summary>
    public sealed record Sacrifice(FileEntry Entry, string Reason);

    /// <summary>
    /// Keep-newest strategy (SPEC §17 "keep most recent"): newest UTC mtime
    /// wins; absolute mtime tie falls through to the size → path tie-breaker.
    /// </summary>
    public static ResolutionPlan Resolve(ConflictGroup group, KeepNewest strategy)
    {
        var ordered = OrderByTieBreak(group.Members, m => m.MtimeUtc);
        return BuildPlan(group, strategy, ordered);
    }

    /// <summary>
    /// Keep-largest strategy (SPEC §17 "keep largest"): largest size wins; absolute
    /// size tie falls through to the mandatory mtime → path tie-breaker.
    /// </summary>
    public static ResolutionPlan Resolve(ConflictGroup group, KeepLargest strategy)
    {
        var ordered = OrderByTieBreak(group.Members, m => m.Size);
        return BuildPlan(group, strategy, ordered);
    }

    /// <summary>
    /// Keep-machine strategy (SPEC §17 "keep version from a given machine"):
    /// the member whose name carries the conflict marker "-DESKTOP-&lt;machineId&gt;"
    /// (SPEC §7), Ordinal, wins. No representative in the group: throws (fail-closed) —
    /// never returns a plausible plan for an unfulfillable request.
    /// </summary>
    public static ResolutionPlan Resolve(ConflictGroup group, KeepMachine strategy)
    {
        var marker = "-DESKTOP-" + strategy.MachineId;
        var representative = group.Members
            .Where(m => Path.GetFileName(m.Path).Contains(marker, StringComparison.Ordinal))
            .ToArray();

        if (representative.Length == 0)
        {
            throw new InvalidOperationException(
                $"keep-machine: no member from machine '{strategy.MachineId}' in the group.");
        }

        // Among representatives from the SAME machine (pathological case), default tie-break.
        var ordered = OrderByTieBreak(representative, m => m.MtimeUtc);

        // Sacrifices: ALL non-winners from the group, in canonical path order.
        return BuildPlan(group, strategy, ordered, exclude: representative[0]);
    }

    /// <summary>
    /// Keep-manual strategy (SPEC §17 "choose manually"): the member whose
    /// path is EXACTLY the user's choice (Ordinal) wins. Choice outside the group:
    /// throws — fail-closed, never a plausible plan for an unfulfillable request.
    /// </summary>
    public static ResolutionPlan Resolve(ConflictGroup group, KeepManual strategy)
    {
        var choice = group.Members
            .FirstOrDefault(m => string.Equals(m.Path, strategy.ChoicePath, StringComparison.Ordinal));

        if (choice is null)
        {
            throw new InvalidOperationException(
                $"keep-manual: choice '{strategy.ChoicePath}' does not belong to the group.");
        }

        return BuildPlan(group, strategy, OrderByTieBreak([choice], m => m.MtimeUtc), exclude: choice);
    }

    /// <summary>
    /// Canonical ordering of winner CANDIDATES: strategy key desc (best first),
    /// then mandatory tie-break mtime desc → size desc → path asc.
    /// First element is the winner; the rest become sacrifices in canonical order.
    /// </summary>
    private static IOrderedEnumerable<FileEntry> OrderByTieBreak<Tkey>(
        IEnumerable<FileEntry> members,
        Func<FileEntry, Tkey> key) where Tkey : IComparable<Tkey>
    {
        return members.OrderByDescending(key)
            .ThenByDescending(m => m.MtimeUtc)
            .ThenByDescending(m => m.Size)
            .ThenBy(m => m.Path, StringComparer.Ordinal);
    }

    private static ResolutionPlan BuildPlan(
        ConflictGroup group,
        ResolutionStrategy strategy,
        IOrderedEnumerable<FileEntry> ordered,
        FileEntry? exclude = null)
    {
        var list = ordered.ToArray();
        if (exclude is null && (list.Length == 0 || list.Length != group.Members.Count))
        {
            throw new InvalidOperationException("Group without members or corrupted: fail-closed.");
        }

        // ConflictGroup contract (Grouping.Group): candidate group always has >= 2
        // members. Entry violating the contract never produces a plan (conservative failure).
        if (group.Members.Count < 2)
        {
            throw new InvalidOperationException(
                $"Candidate group with {group.Members.Count} member(s): contract requires >= 2.");
        }

        var winner = exclude ?? list[0];
        var sacrifices = group.Members
            .Where(m => !ReferenceEquals(m, winner))
            .OrderBy(m => m.Path, StringComparer.Ordinal)
            .Select(m => new Sacrifice(m, ReasonFor(strategy)))
            .ToArray();

        return new ResolutionPlan(group, strategy, winner, sacrifices);
    }

    private static string ReasonFor(ResolutionStrategy strategy) => strategy switch
    {
        KeepNewest => "keep-newest",
        KeepLargest => "keep-largest",
        KeepMachine k => $"keep-machine:{k.MachineId}",
        KeepManual m => "keep-manual",
        _ => throw new InvalidOperationException("Unknown strategy: fail-closed."),
    };
}

/// <summary>Abstract plan strategy (auditable in future manifest).</summary>
public abstract record ResolutionStrategy;

/// <summary>SPEC §17 "keep most recent".</summary>
public sealed record KeepNewest : ResolutionStrategy;

/// <summary>SPEC §17 "keep largest".</summary>
public sealed record KeepLargest : ResolutionStrategy;

/// <summary>SPEC §17 "keep version from a given machine" — machine identified by the conflict marker "-DESKTOP-&lt;host&gt;" (SPEC §7) embedded in the file name.</summary>
public sealed record KeepMachine(string MachineId) : ResolutionStrategy;

/// <summary>SPEC §17 "choose manually" — explicit user choice.</summary>
public sealed record KeepManual(string ChoicePath) : ResolutionStrategy;
