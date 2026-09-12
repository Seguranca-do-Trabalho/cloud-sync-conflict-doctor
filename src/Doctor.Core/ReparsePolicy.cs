namespace Doctor.Core;

/// <summary>
/// Ordered enumerator reparse policy (SPEC §6; threat-model T-02 — mandatory
/// in-depth mitigation). PURE over metadata: decides marking, rejection of
/// paths returning to the same inode and depth ceiling — no I/O.
/// </summary>
public sealed record ReparsePolicy
{
    /// <summary>
    /// Enumeration depth ceiling (threat-model T-02): entries deeper than
    /// this are rejected with a record in <see cref="EnumerationResult.Errors"/> —
    /// never silently (contracts.md R10).
    /// </summary>
    public const int DefaultMaxDepth = 16;

    public int MaxDepth { get; init; } = DefaultMaxDepth;
}
