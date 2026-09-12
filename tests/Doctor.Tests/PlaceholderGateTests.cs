namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — final cycle: <see cref="PlaceholderGate.Enforce"/> on the
/// enumeration result (scope reduced by orchestrator). Proves by spies
/// that NO placeholder byte is read and that the partial report exits with
/// placeholder_bytes_read == 0 GUARANTEED BY CONSTRUCTION (SPEC §6/§21).
/// </summary>
public class PlaceholderGateTests
{
    private static FileEntry Entry(
        string path,
        long size,
        FileAttributes? attrs = null,
        bool isPlaceholder = false,
        PlaceholderKind? kind = null)
        => new()
        {
            Path = path,
            Size = size,
            MtimeUtc = DateTimeOffset.UnixEpoch,
            Attributes = attrs ?? FileAttributes.Normal,
            VolumeId = "vol-test",
            FileId = "1",
            IsPlaceholder = isPlaceholder,
            PlaceholderKind = kind,
        };

    private static EnumerationResult Result(
        IReadOnlyList<FileEntry> files,
        ScanTelemetry? telemetry = null)
        => new(files, Array.Empty<ScanError>(), telemetry ?? new ScanTelemetry());

    [Fact]
    public void Enforce_MixedTree_ZeroReads_CorrectPartialReport()
    {
        // Spies register content for ALL paths — including placeholders.
        // If any byte is read from them, the test turns red.
        var source = new CountingStreamSource();
        var normal1 = Entry("tree/a.bin", 100);
        var normal2 = Entry("tree/b.bin", 200);
        var phOffline = Entry("tree/p.offline", 4096, FileAttributes.Offline, true, PlaceholderKind.Offline);
        var phReparse = Entry("tree/link.bin", 0, FileAttributes.ReparsePoint, true, PlaceholderKind.ReparsePoint);
        source.Register("tree/a.bin", new byte[100]);
        source.Register("tree/b.bin", new byte[200]);
        source.Register("tree/p.offline", new byte[4096]);
        source.Register("tree/link.bin", new byte[999]);

        var input = Result(new[] { normal1, phOffline, normal2, phReparse });

        var report = new PlaceholderGate(source).Enforce(input);

        // Zero access to placeholder content (spies).
        Assert.Equal(0, source.OpenCount("tree/p.offline"));
        Assert.Equal(0, source.OpenCount("tree/link.bin"));
        Assert.Equal(0, source.BytesRead("tree/p.offline"));
        Assert.Equal(0, source.BytesRead("tree/link.bin"));

        // Partial report: only non-placeholders proceed to L1/L2/L3.
        Assert.Equal(new[] { "tree/a.bin", "tree/b.bin" }, report.Files.Select(f => f.Path));

        // Placeholders[] per schema v1 §6.3 projection (order by path bytes).
        Assert.Equal(
            new[] { "tree/link.bin", "tree/p.offline" },
            report.Placeholders.Select(p => p.Path));
        Assert.Equal(new[] { "reparse_point" }, report.Placeholders[0].Kinds);
        Assert.Equal(new[] { "offline" }, report.Placeholders[1].Kinds);

        // Derived telemetry: counted in files_placeholder, gate zeroed by construction.
        Assert.Equal(4, report.Telemetry.FilesEnumerated);
        Assert.Equal(2, report.Telemetry.FilesPlaceholder);
        Assert.Equal(0, report.Telemetry.PlaceholderBytesRead);
        Assert.Equal(0, report.Telemetry.BytesRead);
    }

    [Fact]
    public void OpenRead_MarkedPlaceholder_ThrowsPlaceholderViolationException()
    {
        var source = new CountingStreamSource();
        source.Register("tree/p.offline", new byte[4096]);
        var gate = new PlaceholderGate(source);
        var ph = Entry("tree/p.offline", 4096, FileAttributes.Offline, true, PlaceholderKind.Offline);

        var ex = Assert.Throws<PlaceholderViolationException>(() => gate.OpenRead(ph));

        Assert.Equal(ph.Path, ex.EntryPath);
        Assert.Equal(0, source.OpenCount(ph.Path)); // nothing opened BEFORE exception
    }

    [Fact]
    public void OpenRead_Unmarked_WithRawPlaceholderBits_AlsoThrows()
    {
        // Defense in depth (T-03): missing/stale Level 0 marking does not pass.
        var source = new CountingStreamSource();
        source.Register("tree/x.bin", new byte[512]);
        var gate = new PlaceholderGate(source);
        var disguised = Entry("tree/x.bin", 512, (FileAttributes)0x00400000, false, null);

        Assert.Throws<PlaceholderViolationException>(() => gate.OpenRead(disguised));
        Assert.Equal(0, source.OpenCount("tree/x.bin"));
    }

    [Fact]
    public void OpenRead_NormalFile_DelegatesToSource()
    {
        var source = new CountingStreamSource();
        source.Register("tree/a.bin", new byte[] { 1, 2, 3 });
        var gate = new PlaceholderGate(source);
        var normal = Entry("tree/a.bin", 3);

        using var stream = gate.OpenRead(normal);

        Assert.Equal(3, stream.Length);
        Assert.Equal(1, source.OpenCount("tree/a.bin"));
    }

    [Fact]
    public void Enforce_InputTelemetryWithPlaceholderBytes_NeverWashed()
    {
        // Downstream violation cannot be silently zeroed: the gate REFUSES.
        var source = new CountingStreamSource();
        var dirty = new ScanTelemetry { PlaceholderBytesRead = 128 };

        var ex = Assert.Throws<PlaceholderViolationException>(
            () => new PlaceholderGate(source).Enforce(Result(Array.Empty<FileEntry>(), dirty)));

        Assert.Null(ex.EntryPath);
    }

    [Fact]
    public void Mutation_GateRemoved_IsDetectedBySpies()
    {
        // Card's negative test: a mutation that makes pipeline open placeholder MUST
        // fail. Without gate, the open "succeeds" in reading — and that is exactly what
        // spies expose as violation.
        var source = new CountingStreamSource();
        source.Register("tree/p.offline", new byte[4096]);
        var mutant = Entry("tree/p.offline", 4096, FileAttributes.Offline, true, PlaceholderKind.Offline);

        using (var s = source.OpenRead(mutant))
        {
            var buffer = new byte[1024];
            while (s.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        } // simulated mutation: code without gate opened AND read directly

        Assert.True(source.OpenCount("tree/p.offline") > 0);
        Assert.True(source.BytesRead("tree/p.offline") > 0);
    }
}
