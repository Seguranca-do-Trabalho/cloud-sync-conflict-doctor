namespace Doctor.Core;

/// <summary>
/// Name reserved by the tool at the scanned root (ADR-0002; ADR-0010 §1):
/// <c>&lt;root&gt;/ConflictDoctor/</c> — quarantine §18/SPEC and operation metadata.
/// Single source of the literal: quarantine publishes under this name and Level 0
/// enumeration excludes this subtree from the scan by canonical path bytes prefix
/// (SEG-12, T-15 addendum of GATE 5 audit; threat-model T-06 case, rule R1).
/// Changing the value here changes both sides of the contract simultaneously.
/// </summary>
public static class ReservedSubtree
{
    /// <summary>Name of the reserved directory at the scanned root.</summary>
    public const string DirectoryName = "ConflictDoctor";
}
