namespace Doctor.Core;

/// <summary>
/// Diff region type (orchestrator decision, card T23 — SPEC §16; ADR-0011):
/// <see cref="Equal"/> matched span; <see cref="Added"/> only in right document;
/// <see cref="Removed"/> only in left; <see cref="Changed"/> substitute pair
/// (removal paired 1:1 with addition within the same block).
/// </summary>
public enum RegionKind
{
    Equal,
    Added,
    Removed,
    Changed,
}

/// <summary>
/// Span of divergence/equality between two documents. 0-based indices with
/// ELEMENT COUNTS (never end index). Consecutive regions partition
/// both documents in order: sum(LeftCount) == left lines and
/// sum(RightCount) == right lines.
/// </summary>
public sealed record DiffRegion(
    RegionKind Kind,
    int LeftStart,
    int LeftCount,
    int RightStart,
    int RightCount);
