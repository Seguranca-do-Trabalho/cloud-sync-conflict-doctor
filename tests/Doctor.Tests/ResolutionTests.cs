namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T14 (t_83b9d360) — resolution engine (SPEC §17): deterministic ResolutionPlan per
/// ScanPipeline group, strategies KeepNewest/KeepLargest/KeepMachine/KeepManual.
/// NO file operations: only the PLAN (winner + sacrifices). Mandatory tie-break
/// mtime → size → path byte-wise (ADR-0003), never first-seen.
/// Synthetic entries without real filesystem: FileEntry is immutable metadata (Level 0).
/// </summary>
public sealed class ResolutionTests
{
    private static FileEntry Entry(string path, long size, string mtimeIso) => new()
    {
        Path = path,
        Size = size,
        MtimeUtc = DateTimeOffset.Parse(mtimeIso, styles: System.Globalization.DateTimeStyles.AssumeUniversal),
        Attributes = FileAttributes.Normal,
        VolumeId = "t14-volume",
        FileId = path,
    };

    // ---------------------------------------------------------------- KeepNewest

    [Fact]
    public void KeepNewest_DistinctMtimes_MostRecentWins_SacrificesInCanonicalOrder()
    {
        var group = new ConflictGroup("report", 100,
        [
            Entry("/root/report.txt", 100, "2026-08-20T10:00:00Z"),
            Entry("/root/report (1).txt", 100, "2026-08-22T12:00:00Z"), // most recent
            Entry("/root/report-DESKTOP-ABC123.txt", 100, "2026-08-21T11:00:00Z"),
        ]);

        var plan = Resolution.Resolve(group, new KeepNewest());

        Assert.Equal("/root/report (1).txt", plan.Winner.Path);
        Assert.Equal(
            new[] { "/root/report-DESKTOP-ABC123.txt", "/root/report.txt" },
            plan.Sacrifices.Select(s => s.Entry.Path).ToArray());
    }

    // ---------------------------------------------------------------- KeepLargest

    [Fact]
    public void KeepLargest_DistinctSizes_LargerWins_AuditableReason()
    {
        var group = new ConflictGroup("spreadsheet", 0,
        [
            Entry("/root/spreadsheet.xlsx", 300, "2026-08-20T10:00:00Z"),
            Entry("/root/spreadsheet (1).xlsx", 500, "2026-08-19T09:00:00Z"), // larger
            Entry("/root/spreadsheet~.xlsx", 400, "2026-08-21T08:00:00Z"),
        ]);

        var plan = Resolution.Resolve(group, new KeepLargest());

        Assert.Equal("/root/spreadsheet (1).xlsx", plan.Winner.Path);
        Assert.Equal(2, plan.Sacrifices.Count);
        Assert.All(plan.Sacrifices, s => Assert.Equal("keep-largest", s.Reason));
    }

    // ---------------------------------------------------------------- KeepMachine

    [Fact]
    public void KeepMachine_DesktopMarkedInName_RequestedMachineVersionWins()
    {
        var group = new ConflictGroup("contract", 0,
        [
            Entry("/root/contract-DESKTOP-ABC123.txt", 100, "2026-08-22T12:00:00Z"),
            Entry("/root/contract-DESKTOP-XYZ789.txt", 100, "2026-08-20T10:00:00Z"),
            Entry("/root/contract (1).txt", 100, "2026-08-21T11:00:00Z"), // no mark
        ]);

        var plan = Resolution.Resolve(group, new KeepMachine("XYZ789"));

        Assert.Equal("/root/contract-DESKTOP-XYZ789.txt", plan.Winner.Path);
        Assert.Equal(
            new[] { "/root/contract (1).txt", "/root/contract-DESKTOP-ABC123.txt" },
            plan.Sacrifices.Select(s => s.Entry.Path).ToArray());
        Assert.All(plan.Sacrifices, s => Assert.Equal("keep-machine:XYZ789", s.Reason));
    }

    // ---------------------------------------------------------------- KeepManual

    [Fact]
    public void KeepManual_ChoiceWithinGroup_UserChoiceWins()
    {
        var group = new ConflictGroup("handbook", 0,
        [
            Entry("/root/handbook.pdf", 100, "2026-08-22T12:00:00Z"), // most recent
            Entry("/root/handbook (1).pdf", 100, "2026-08-20T10:00:00Z"),
        ]);

        var plan = Resolution.Resolve(group, new KeepManual("/root/handbook (1).pdf"));

        Assert.Equal("/root/handbook (1).pdf", plan.Winner.Path);
        Assert.Equal(
            new[] { "/root/handbook.pdf" },
            plan.Sacrifices.Select(s => s.Entry.Path).ToArray());
        Assert.All(plan.Sacrifices, s => Assert.Equal("keep-manual", s.Reason));
    }

    [Fact]
    public void KeepManual_ChoiceOutsideGroup_FailClosed()
    {
        var group = new ConflictGroup("handbook", 0,
        [
            Entry("/root/handbook.pdf", 100, "2026-08-22T12:00:00Z"),
            Entry("/root/handbook (1).pdf", 100, "2026-08-20T10:00:00Z"),
        ]);

        Assert.Throws<InvalidOperationException>(
            () => Resolution.Resolve(group, new KeepManual("/other/file.pdf")));
    }

    // ---------------------------------------------------------------- Ties

    [Fact]
    public void TripleTie_IdenticalMtimes_TieBrokenBySizeThenPathByteWise()
    {
        // IDENTICAL mtimes: decision can never be first-seen (SPEC §17; ADR-0003).
        var commonMtime = "2026-08-22T12:00:00Z";
        var group = new ConflictGroup("inventory", 0,
        [
            Entry("/root/inventory-B.txt", 200, commonMtime),
            Entry("/root/inventory-A.txt", 300, commonMtime), // wins: larger size
            Entry("/root/inventory-C.txt", 100, commonMtime),
        ]);

        var plan = Resolution.Resolve(group, new KeepNewest());

        Assert.Equal("/root/inventory-A.txt", plan.Winner.Path);
        Assert.Equal(
            new[] { "/root/inventory-B.txt", "/root/inventory-C.txt" },
            plan.Sacrifices.Select(s => s.Entry.Path).ToArray());
    }

    [Fact]
    public void AbsoluteTie_SameMtimeAndSize_SmallerPathByteWiseWins()
    {
        var commonMtime = "2026-08-22T12:00:00Z";
        long commonSize = 300;
        var group = new ConflictGroup("inventory", 0,
        [
            Entry("/root/inventory-b.txt", commonSize, commonMtime),
            Entry("/root/inventory-a.txt", commonSize, commonMtime),
        ]);

        var plan = Resolution.Resolve(group, new KeepLargest());

        // ASCENDING path byte-wise: "a" < "b" in Ordinal.
        Assert.Equal("/root/inventory-a.txt", plan.Winner.Path);
        Assert.Equal(
            new[] { "/root/inventory-b.txt" },
            plan.Sacrifices.Select(s => s.Entry.Path).ToArray());
    }

    // ---------------------------------------------------------------- Fail-closed

    [Fact]
    public void KeepMachine_MachineAbsentFromGroup_FailClosed()
    {
        var group = new ConflictGroup("contract", 0,
        [
            Entry("/root/contract-DESKTOP-ABC123.txt", 100, "2026-08-22T12:00:00Z"),
            Entry("/root/contract (1).txt", 100, "2026-08-20T10:00:00Z"),
        ]);

        Assert.Throws<InvalidOperationException>(
            () => Resolution.Resolve(group, new KeepMachine("ZZZ999")));
    }

    [Fact]
    public void CorruptedGroup_SingleMember_FailClosedNoPlan()
    {
        // ConflictGroup requires >=2 members; entry violating the contract never generates
        // a plausible plan — throws (SPEC §2.1 conservative failure).
        var group = new ConflictGroup("lonely", 0,
        [
            Entry("/root/lonely.txt", 100, "2026-08-22T12:00:00Z"),
        ]);

        Assert.Throws<InvalidOperationException>(
            () => Resolution.Resolve(group, new KeepNewest()));
    }

    // ---------------------------------------------------------------- Determinism

    [Fact]
    public void PlanIndependentOfPhysicalInputOrder_NeverFirstSeen()
    {
        var commonMtime = "2026-08-22T12:00:00Z";
        FileEntry[] members =
        [
            Entry("/root/note-B.txt", 200, commonMtime),
            Entry("/root/note-A.txt", 200, commonMtime), // wins by path (absolute tie with B)
            Entry("/root/note-C.txt", 100, "2026-08-21T11:00:00Z"),
            Entry("/root/note-D.txt", 300, "2026-08-20T10:00:00Z"),
        ];

        var forward = Resolution.Resolve(new ConflictGroup("note", 0, members), new KeepNewest());
        var reverse = Resolution.Resolve(
            new ConflictGroup("note", 0, members.Reverse().ToArray()), new KeepNewest());

        Assert.Equal(forward.Winner.Path, reverse.Winner.Path);
        Assert.Equal(
            forward.Sacrifices.Select(s => s.Entry.Path).ToArray(),
            reverse.Sacrifices.Select(s => s.Entry.Path).ToArray());
        Assert.Equal("/root/note-A.txt", forward.Winner.Path);
    }
}
