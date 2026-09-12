namespace Doctor.Core;

/// <summary>
/// Pipeline L0→L3 scan result (T12 — reduced orchestrator scope):
/// Level 1 candidate groups and Level 3 final verdicts — identical
/// duplicates (full hash equal across all group members) and real conflicts
/// (divergent full hash), class by class, always in canonical order
/// (<see cref="PathOrder"/>; SPEC §3, ADR-0004 rule 1). Not the final report
/// (schema v1 / IReportWriter): it is the serial decision type consumed by this
/// card's determinism tests.
/// </summary>
public sealed record ScanResult(
    IReadOnlyList<ConflictGroup> Groups,
    IReadOnlyList<IdenticalDuplicate> IdenticalDuplicates,
    IReadOnlyList<RealConflict> RealConflicts,
    IReadOnlyList<UnresolvedGroup>? UnresolvedGroups = null)
{
    /// <summary>Groups rejected due to unverifiable hash (S11-5). Never null.</summary>
    public IReadOnlyList<UnresolvedGroup> UnresolvedGroups { get; init; } =
        UnresolvedGroups ?? [];
}

/// <summary>
/// Group whose L3 decision was rejected due to inability to produce a complete hash
/// for at least one member (S11-5 fail-closed, threat T-11): without a verified full
/// hash, no classification is issued — the group becomes neither duplicate nor conflict
/// and the pair is never eligible for resolution/quarantine. Reason auditable per member.
/// </summary>
public sealed record UnresolvedGroup(
    string NormalizedBaseName,
    long SizeBytes,
    IReadOnlyList<UnresolvedMember> Members);

/// <summary>Member without a verifiable full hash, with the rejection reason.</summary>
public sealed record UnresolvedMember(string Path, string Reason);

/// <summary>
/// Identical duplicate class (schema v1 §6.1): common full BLAKE3 hash +
/// members in canonical path order. Formed only from groups whose full hashes
/// are ALL equal; a group never generates an entry here and in
/// <see cref="RealConflict"/> (§6.1/§6.2 — per-group classification mutually exclusive).
/// </summary>
public sealed record IdenticalDuplicate(string Hash, long SizeBytes, IReadOnlyList<FileEntry> Files);

/// <summary>
/// Real conflict (schema v1 §6.2): candidate group with at least two distinct full hashes
/// after Level 2. Covers ALL group members, with each member's individual full hash
/// in canonical path order; internally identical subsets are reconstructable from hashes.
/// </summary>
public sealed record RealConflict(
    string NormalizedBaseName,
    long SizeBytes,
    IReadOnlyList<ConflictMember> Files);

/// <summary>Member of a real conflict: canonical path + individual full hash.</summary>
public sealed record ConflictMember(string Path, string Hash);

/// <summary>
/// Deterministic L0→L3 pipeline (SPEC §5, §11; ADR-0004; docs/contracts.md):
/// enumerates (L0), groups candidates by normalized_base_name + size (L1 — Grouping),
/// partially hashes members of groups with 2+ items (L2 — IHasher.PartialHash)
/// and applies full BLAKE3 ONLY on partial collisions (L3 —
/// Blake3Hasher.FullHashBlake3 via IStreamSource). Every decision is serial over
/// collections in canonical order; no decision depends on arrival order or
/// thread completion order (ADR-0004 rules 1-2). Placeholder never enters
/// L1: <see cref="PlaceholderGate"/> removes before grouping and the policy is
/// reclassified at each hash gate (SPEC §6).
/// </summary>
public sealed class ScanPipeline
{
    /// <summary>Unverifiable hash sentinel (S11-5): prefix + reason.</summary>
    public const string UnresolvedPrefix = "UNRESOLVED::";

    private readonly IFileEnumerator _enumerator;
    private readonly IHasher _hasher;
    private readonly IStreamSource? _streams;
    private readonly PlaceholderGate _gate;

    /// <param name="enumerator">L0 source. If not <see cref="OrderedFileEnumerator"/>,
    /// the pipeline reorders by canonical order — output ordering does not depend on the
    /// enumerator (SPEC §3).</param>
    /// <param name="hasher">L2 (partial) hasher. In production goes through
    /// <see cref="PlaceholderGuardedHasher"/>; the pipeline reclassifies for safety.</param>
    /// <param name="streams">L3 single content source (contracts.md IStreamSource).
    /// Null only when <paramref name="hasher"/> is a test double that doesn't read
    /// content — in that case the opening gate is the hasher itself.</param>
    public ScanPipeline(IFileEnumerator enumerator, IHasher hasher, IStreamSource? streams = null)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _streams = streams;
        _gate = new PlaceholderGate(streams ?? new RejectingStreamSource());
    }

    /// <summary>
    /// Runs L0→L3 deterministically: same tree ⇒ same
    /// <see cref="ScanResult"/> byte by byte, regardless of physical enumeration
    /// order (SPEC §20; automated proof at DET-03 minimum of this card).
    /// </summary>
    public ScanResult Run(string rootPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // L0 — enumeration + canonical order guaranteed at the pipeline boundary.
        var enumeration = _enumerator is OrderedFileEnumerator
            ? _enumerator.Enumerate(rootPath, ct)
            : new OrderedFileEnumerator(_enumerator).Enumerate(rootPath, ct);

        // Placeholder gate (SPEC §6/§21): separates safe flow from Placeholders[].
        var safe = _gate.Enforce(enumeration);

        // L1 — candidate grouping: serial, input already in canonical order
        // (Grouping reorders internally as structural defense — Grouping.Group).
        var groups = Grouping.Group(safe.Files);

        var identical = new List<IdenticalDuplicate>();
        var conflicts = new List<RealConflict>();
        var unresolved = new List<UnresolvedGroup>();

        // L2 + L3 — serial decision on already ordered groups (base in bytes, size).
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            var partial = group.Members
                .Select(m => (Member: m, Partial: HashGuarded(() => _hasher.PartialHash(m, ct), m)))
                .ToArray();

            // Partial collision = at least two members with the SAME partial hash.
            // Without partial collision there is no duplicate candidate: group does not
            // survive L2 and no full hash happens (ADR-0004: L3 only on survivors).
            // S11-5 fail-closed: unverifiable partial hash (sentinel) forces the
            // entire group to unresolved — no decision on missing data.
            var hasUnresolvedPartial = partial.Any(p =>
                p.Partial.StartsWith(UnresolvedPrefix, StringComparison.Ordinal));

            if (hasUnresolvedPartial)
            {
                unresolved.Add(new UnresolvedGroup(
                    group.NormalizedBaseName,
                    group.SizeBytes,
                    partial
                        .Where(p => p.Partial.StartsWith(UnresolvedPrefix, StringComparison.Ordinal))
                        .Select(p => new UnresolvedMember(p.Member.Path, p.Partial[UnresolvedPrefix.Length..]))
                        .ToArray()));
                continue;
            }

            var hasPartialCollision = partial
                .GroupBy(p => p.Partial, StringComparer.Ordinal)
                .Any(g => g.Count() >= 2);

            if (!hasPartialCollision)
            {
                continue;
            }

            // L3 — full hash of L2 survivors, decided serially over the
            // ordered list (ADR-0004 rule 2). The verdict covers ALL group
            // members with individual hash (schema v1 §6.2): distinct partial implies
            // distinct content, so each member participates in the final record.
            var full = new (FileEntry Member, string Full)[partial.Length];
            for (var i = 0; i < partial.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                full[i] = (partial[i].Member, HashGuarded(() => FullHash(partial[i].Member, ct), partial[i].Member));
            }

            ClassifyGroup(group, full, identical, conflicts, unresolved);
        }

        identical.Sort((a, b) => string.CompareOrdinal(a.Files[0].Path, b.Files[0].Path));
        conflicts.Sort((a, b) => string.CompareOrdinal(a.Files[0].Path, b.Files[0].Path));
        unresolved.Sort((a, b) => string.CompareOrdinal(a.Members[0].Path, b.Members[0].Path));

        return new ScanResult(groups, identical.ToArray(), conflicts.ToArray(), unresolved.ToArray());
    }

    /// <summary>
    /// Per-group verdict, mutually exclusive (schema v1 §6.1/§6.2): all full hashes
    /// equal ⇒ <see cref="IdenticalDuplicate"/>; at least two distinct ⇒
    /// <see cref="RealConflict"/> covering ALL members.
    /// </summary>
    private void ClassifyGroup(
        ConflictGroup group,
        (FileEntry Member, string Full)[] full,
        List<IdenticalDuplicate> identical,
        List<RealConflict> conflicts,
        List<UnresolvedGroup> unresolved)
    {
        // S11-5 fail-closed (T-11): member whose full hash could not be verified
        // rejects the ENTIRE GROUP's decision — never classify with missing data.
        var failed = full
            .Where(f => f.Full.StartsWith(UnresolvedPrefix, StringComparison.Ordinal))
            .Select(f => new UnresolvedMember(f.Member.Path, f.Full[UnresolvedPrefix.Length..]))
            .ToArray();

        if (failed.Length > 0)
        {
            unresolved.Add(new UnresolvedGroup(
                group.NormalizedBaseName,
                group.SizeBytes,
                failed));
            return;
        }

        var distinctFull = full.Select(f => f.Full).Distinct(StringComparer.Ordinal).Count();

        if (distinctFull <= 1)
        {
            identical.Add(new IdenticalDuplicate(
                full[0].Full,
                group.SizeBytes,
                full.Select(f => f.Member).ToArray()));
        }
        else
        {
            conflicts.Add(new RealConflict(
                group.NormalizedBaseName,
                group.SizeBytes,
                full.Select(f => new ConflictMember(f.Member.Path, f.Full)).ToArray()));
        }
    }

    /// <summary>
    /// L3 via single content source (contracts.md). With injected stream source,
    /// <see cref="Blake3Hasher.FullHashBlake3"/> applies the placeholder gate before
    /// opening; without stream source (test hasher), the gate already lives in the hasher.
    /// </summary>
    private string FullHash(FileEntry entry, CancellationToken ct) =>
        _streams is null
            ? _hasher.FullHash(entry, ct)
            : Blake3Hasher.FullHashBlake3(_gate, entry, ct);

    /// <summary>
    /// Defensive reclassification at decision point (SPEC §6; threat T-03): missing or
    /// stale L0 marking never becomes a read — <see cref="PlaceholderPolicy"/> has the
    /// last word before any hash.
    /// </summary>
    private static string HashGuarded(Func<string> compute, FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }

        try
        {
            return compute();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlaceholderViolationException)
        {
            // S11-5 fail-closed: read/hash failure never aborts the scan and NEVER
            // produces a decision — the member carries the reason as an auditable
            // sentinel consumed by ClassifyGroup (entire group becomes UnresolvedGroup).
            return UnresolvedPrefix + ex.Message;
        }
    }

    /// <summary>
    /// Content source that refuses all openings: used only to satisfy
    /// <see cref="PlaceholderGate"/> when the test hasher doesn't read bytes. Any
    /// opening here is a gate bug, not a legitimate path.
    /// </summary>
    private sealed class RejectingStreamSource : IStreamSource
    {
        public Stream OpenRead(FileEntry entry) =>
            throw new PlaceholderViolationException(entry.Path, "missing content source in pipeline:");
    }
}
