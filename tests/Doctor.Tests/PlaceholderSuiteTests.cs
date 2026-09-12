namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T22 (t_807d2357) — PLACEHOLDER ENFORCEMENT SUITE (PLH-01..PLH-04).
/// Proves, through open spies (IStreamSource mocks that count opens),
/// that the pipeline NEVER reads placeholders: neither during scan, nor by post-gate mutation.
/// Complements PlaceholderGateTests/PlaceholderPipelineIntegrationTests with the four
/// contractual guarantees of the card. NOT production code.
///
/// PLH-01 — no File.Open on placeholder during scan;
/// PLH-02 — PlaceholderReadException thrown BEFORE any I/O;
/// PLH-03 — PlaceholderViolationException if telemetry arrives with bytes != 0;
/// PLH-04 — post-gate mutation fails (structural negative test).
/// </summary>
public class PlaceholderSuiteTests
{
    private static FileEntry Entry(
        string path,
        long size,
        FileAttributes? attrs = null,
        bool isPlaceholder = false,
        PlaceholderKind? kind = null,
        string fileId = "1")
        => new()
        {
            Path = path,
            Size = size,
            MtimeUtc = DateTimeOffset.UnixEpoch,
            Attributes = attrs ?? FileAttributes.Normal,
            VolumeId = "vol-test",
            FileId = fileId,
            IsPlaceholder = isPlaceholder,
            PlaceholderKind = kind,
        };

    private static EnumerationResult Result(
        IReadOnlyList<FileEntry> files,
        ScanTelemetry? telemetry = null)
        => new(files, Array.Empty<ScanError>(), telemetry ?? new ScanTelemetry());

    /// <summary>
    /// PLH-01 — During a complete scan (L0→L3) of a mixed tree (normal content +
    /// placeholders registered in the source), the open count on each
    /// placeholder is ZERO. The spy source has content available for placeholders:
    /// if any product path opens a stream on them, the test turns red
    /// (SPEC §6 "DO NOT TOUCH"; gate placeholder_bytes_read == 0).
    /// </summary>
    [Fact]
    public void Plh01_ScanMixedTree_NoStreamOpensOnPlaceholders()
    {
        var source = new CountingStreamSource();
        var placeholders = new[]
        {
            ("tree/p.offline", PlaceholderKind.Offline, FileAttributes.Offline),
            ("tree/p.recall", PlaceholderKind.RecallOnOpen, PlaceholderPolicy.RecallOnOpen),
            ("tree/p.data", PlaceholderKind.RecallOnDataAccess, PlaceholderPolicy.RecallOnDataAccess),
            ("tree/p.reparse", PlaceholderKind.ReparsePoint, FileAttributes.ReparsePoint),
        };
        foreach (var (path, _, _) in placeholders)
        {
            source.Register(path, new byte[2048]);
        }

        // Identical content pair, same name and size ⇒ collides at L1 and survives L2/L3.
        source.Register("tree/a/Report.bin", new byte[] { 1, 2, 3 });
        source.Register("tree/b/Report.bin", new byte[] { 1, 2, 3 });

        var entries = new List<FileEntry>();
        var placeholderIds = new[] { "p_offline", "p_recall", "p_data", "p_reparse" };
        foreach (var i in Enumerable.Range(0, placeholders.Length))
        {
            var (path, kind, attrs) = placeholders[i];
            // Missing L0 marking OR present marking changes nothing: the double gate covers both.
            var marked = path != "tree/p.recall";
            entries.Add(Entry(path, 2048, attrs, marked, marked ? kind : null, placeholderIds[i]));
        }

        entries.Add(Entry("tree/a/Report.bin", 3, fileId: "fa"));
        entries.Add(Entry("tree/b/Report.bin", 3, fileId: "fb"));

        var hasher = CountingHasher.Using(new PlaceholderGate(source));
        var pipeline = new ScanPipeline(new FakeEnumerator(Result(entries)), hasher, new PlaceholderGate(source));

        var result = pipeline.Run("tree");

        // Scan came out intact: identical duplicate detected, no false conflicts.
        Assert.Single(result.IdenticalDuplicates);
        Assert.Empty(result.RealConflicts);

        // Core proof: zero opens on EACH placeholder, across the four kinds.
        foreach (var (path, _, _) in placeholders)
        {
            Assert.Equal(0, source.OpenCount(path));
            Assert.Equal(0, source.BytesRead(path));
        }

        // And legitimate content was read normally (scan actually went through L3).
        Assert.True(source.OpenCount("tree/a/Report.bin") >= 1);
        Assert.True(source.OpenCount("tree/b/Report.bin") >= 1);
    }

    /// <summary>
    /// PLH-02 — PlaceholderReadException fires BEFORE any I/O: the spy hasher
    /// passes through the gate and the spy source counts opens; when attempting to hash a placeholder
    /// (L0 flag and, separately, only with raw bits — double gate), no open is
    /// recorded BEFORE the exception (gate→I/O order proven by count == 0).
    /// </summary>
    [Fact]
    public void Plh02_HashOnPlaceholder_ThrowsBeforeAnyIo()
    {
        var source = new CountingStreamSource();
        source.Register("tree/p.offline", new byte[1024]);

        // Case 1: L0 flag marked.
        var marked = Entry("tree/p.offline", 1024, FileAttributes.Offline, true, PlaceholderKind.Offline);
        var hasherMarked = new PlaceholderGuardedHasher(CountingHasher.Using(source));

        var ex = Assert.Throws<PlaceholderReadException>(() => hasherMarked.FullHash(marked));

        Assert.Equal("tree/p.offline", ex.EntryPath);
        Assert.Equal(0, source.OpenCount("tree/p.offline"));
        Assert.Equal(0, source.BytesRead("tree/p.offline"));

        // Case 2: marking absent, raw bits reveal placeholder (stale marking never passes).
        var disguised = Entry("tree/p.offline", 1024, FileAttributes.Offline);
        var hasherDisguised = new PlaceholderGuardedHasher(CountingHasher.Using(source));

        Assert.Throws<PlaceholderReadException>(() => hasherDisguised.PartialHash(disguised));

        Assert.Equal(0, source.OpenCount("tree/p.offline"));
        Assert.Equal(0, source.BytesRead("tree/p.offline"));

        // Positive control: normal file DELEGATES to source and opens exactly once.
        source.Register("tree/ok.bin", new byte[] { 9 });
        hasherMarked.FullHash(Entry("tree/ok.bin", 1));
        Assert.Equal(1, source.OpenCount("tree/ok.bin"));
    }

    /// <summary>
    /// PLH-03 — Telemetry that arrives at the gate with placeholder_bytes_read != 0 is a security
    /// violation, not data: PlaceholderViolationException WITHOUT path, the gate never
    /// "washes" the counter and the output (when present) keeps the value zeroed by construction.
    /// </summary>
    [Fact]
    public void Plh03_TelemetryWithPlaceholderBytes_ThrowsViolationAndNeverWashed()
    {
        var source = new CountingStreamSource();

        foreach (var dirty in new[]
                 {
                     new ScanTelemetry { PlaceholderBytesRead = 1 },
                     new ScanTelemetry { PlaceholderBytesRead = 4096 },
                     new ScanTelemetry { PlaceholderBytesRead = -3 }, // not even negative passes
                 })
        {
            var ex = Assert.Throws<PlaceholderViolationException>(
                () => new PlaceholderGate(source).Enforce(Result(Array.Empty<FileEntry>(), dirty)));

            Assert.Null(ex.EntryPath); // telemetry violation does not carry path
        }

        // Control: clean telemetry passes through and exits with placeholder_bytes_read == 0.
        var clean = new PlaceholderGate(source).Enforce(Result(Array.Empty<FileEntry>(), new ScanTelemetry()));
        Assert.Equal(0, clean.Telemetry.PlaceholderBytesRead);
    }

    /// <summary>
    /// PLH-04 — Post-gate mutation fails: removing/bypassing the gate makes the violation visible.
    /// Structured in three proofs:
    /// (a) without gate, the placeholder open "succeeds" in reading — exactly the state that
    ///     the spies report (count > 0 ⇒ any suite with these tests turns red);
    /// (b) the real gate refuses the same open with PlaceholderViolationException BEFORE
    ///     touching the source (count stays 0);
    /// (c) final report invariant: placeholder_bytes_read == 0 after Enforce.
    /// </summary>
    [Fact]
    public void Plh04_PostGateMutation_Fails_DetectedBySpies()
    {
        const string path = "tree/p.offline";

        // (a) Simulated mutant: post-gate code without protection opens and reads the placeholder.
        var mutantSource = new CountingStreamSource();
        mutantSource.Register(path, new byte[512]);
        var placeholder = Entry(path, 512, FileAttributes.Offline, true, PlaceholderKind.Offline);

        using (var s = mutantSource.OpenRead(placeholder))
        {
            var buffer = new byte[256];
            while (s.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }

        Assert.True(mutantSource.OpenCount(path) > 0, "mutation must succeed in opening (otherwise test proves nothing)");
        Assert.True(mutantSource.BytesRead(path) > 0);

        // (b) Real product: same access, now under gate, fails BEFORE the source.
        var productSource = new CountingStreamSource();
        productSource.Register(path, new byte[512]);
        var gate = new PlaceholderGate(productSource);

        Assert.Throws<PlaceholderViolationException>(() => gate.OpenRead(placeholder));
        Assert.Equal(0, productSource.OpenCount(path));
        Assert.Equal(0, productSource.BytesRead(path));

        // (c) Structural post-gate invariant: derived telemetry locks counter at zero.
        var enumeration = Result(new[]
        {
            placeholder,
            Entry("tree/a.bin", 1),
        });
        var partial = new PlaceholderGate(new CountingStreamSource()).Enforce(enumeration);

        Assert.Single(partial.Placeholders);
        Assert.Single(partial.Files);
        Assert.Equal(0, partial.Telemetry.PlaceholderBytesRead);
    }

    /// <summary>Fake L0 for the pipeline (just returns the ready result).</summary>
    private sealed class FakeEnumerator : IFileEnumerator
    {
        private readonly EnumerationResult _result;

        public FakeEnumerator(EnumerationResult result) => _result = result;

        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) => _result;
    }
}
