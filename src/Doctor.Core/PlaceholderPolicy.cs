namespace Doctor.Core;

/// <summary>
/// Classification of why an entry is a placeholder (SPEC §6).
/// Classification precedence order: OFFLINE → RECALL_ON_OPEN →
/// RECALL_ON_DATA_ACCESS → ReparsePoint.
/// </summary>
public enum PlaceholderKind
{
    Offline,
    RecallOnOpen,
    RecallOnDataAccess,
    ReparsePoint,
}

/// <summary>
/// Critical security rule (SPEC §6): entry marked as placeholder is "DO NOT TOUCH" —
/// never open, never hash, never obtain content. PURE policy over the metadata already
/// collected at Level 0: no I/O, no file opening, no platform dependency.
/// </summary>
public static class PlaceholderPolicy
{
    // FILE_ATTRIBUTE_* bits that don't exist in System.IO.FileAttributes in .NET 8:
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;       // FILE_ATTRIBUTE_RECALL_ON_OPEN
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    /// <summary>True if the entry can NEVER have its content read.</summary>
    public static bool IsPlaceholder(FileEntry entry) => Classify(entry) is not null;

    /// <summary>
    /// Overload on raw attributes (without <see cref="FileEntry"/>): used by
    /// points that decide BEFORE building the entry (e.g.: descent refusal in
    /// directory with reparse — ADR-0004 rule 4). Keeps the "do not touch"
    /// bits centralized here, the product's single decision point.
    /// </summary>
    public static bool IsPlaceholder(FileAttributes attributes) =>
        (attributes & FileAttributes.Offline) != 0
        || (attributes & RecallOnOpen) != 0
        || (attributes & RecallOnDataAccess) != 0
        || (attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Returns the placeholder kind or null if the entry is safe to read.
    /// A single bit is sufficient: any of the four triggers "DO NOT TOUCH".
    /// T16 rule (fail-closed, SPEC §6): marking already done at ORIGIN
    /// (<see cref="FileEntry.PlaceholderKind"/> — T04 sidecar convention today,
    /// native hook tomorrow) is HIGHEST AUTHORITY and is never erased by subsequent
    /// enumerator projection: whoever classifies first decides; raw bits only
    /// ADD suspicion, never remove marking. In native Windows tree the
    /// behavior is identical to before (null kind ⇒ decision by bits).
    /// </summary>
    public static PlaceholderKind? Classify(FileEntry entry)
    {
        if (entry.PlaceholderKind is { } fromOrigin)
        {
            return fromOrigin;
        }

        var a = entry.Attributes;

        if ((a & FileAttributes.Offline) != 0)                 return PlaceholderKind.Offline;            // 0x1000
        if ((a & RecallOnOpen) != 0)                           return PlaceholderKind.RecallOnOpen;       // 0x40000
        if ((a & RecallOnDataAccess) != 0)                     return PlaceholderKind.RecallOnDataAccess; // 0x400000
        if ((a & FileAttributes.ReparsePoint) != 0)            return PlaceholderKind.ReparsePoint;       // 0x400

        return null;
    }
}
