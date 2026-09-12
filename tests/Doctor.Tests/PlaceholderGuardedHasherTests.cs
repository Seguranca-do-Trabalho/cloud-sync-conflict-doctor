namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — PLH-01 and negative mutation test (docs/test-strategy.md §3.2;
/// SPEC §6/§21; ADR-0005 item 6; threat-model T-03).
/// Spies prove: no hash function called over placeholder, no stream
/// opened over placeholder — therefore zero bytes read.
/// </summary>
public class PlaceholderGuardedHasherTests
{
    private static FileEntry Entry(
        string path,
        long size = 10,
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

    public static TheoryData<FileAttributes, PlaceholderKind> Spec21Reasons => new()
    {
        { FileAttributes.Offline, PlaceholderKind.Offline },
        { (FileAttributes)0x00040000, PlaceholderKind.RecallOnOpen },
        { (FileAttributes)0x00400000, PlaceholderKind.RecallOnDataAccess },
        { FileAttributes.ReparsePoint, PlaceholderKind.ReparsePoint },
    };

    [Theory]
    [MemberData(nameof(Spec21Reasons))]
    public void Gate_Placeholder_DoesNotCallHash_DoesNotOpenStream_DoesNotReadBytes(
        FileAttributes attrs, PlaceholderKind kind)
    {
        // PLH-01: each §21 reason, in both IHasher contract operations.
        foreach (var useFull in new[] { false, true })
        {
            var source = new CountingStreamSource();
            var inner = CountingHasher.Using(source);
            var guarded = new PlaceholderGuardedHasher(inner);
            source.Register("tree/ph.bin", new byte[2048]); // if opened, the spy records

            var entry = Entry("tree/ph.bin", size: 2048, attrs: attrs, isPlaceholder: true, kind: kind);

            var ex = useFull
                ? Assert.Throws<PlaceholderReadException>(() => guarded.FullHash(entry))
                : Assert.Throws<PlaceholderReadException>(() => guarded.PartialHash(entry));

            Assert.Equal(entry.Path, ex.EntryPath);
            Assert.Equal(0, inner.Calls.GetValueOrDefault(entry.Path)); // no hash function (SPEC §21)
            Assert.Equal(0, source.OpenCount(entry.Path));              // no stream opened
            Assert.Equal(0, source.TotalBytes);                         // therefore, zero bytes read
        }
    }

    [Fact]
    public void Gate_UnmarkedEntry_WithPlaceholderBits_IsAlsoBlocked()
    {
        // Defense in depth (threat-model T-03): missing/stale Level 0 marking
        // does not pass — the gate reclassifies by raw attributes BEFORE any open.
        var source = new CountingStreamSource();
        var inner = CountingHasher.Using(source);
        var guarded = new PlaceholderGuardedHasher(inner);
        source.Register("tree/offline.bin", new byte[512]);

        var entry = Entry("tree/offline.bin", size: 512, attrs: FileAttributes.Offline, isPlaceholder: false);

        var exP = Assert.Throws<PlaceholderReadException>(() => guarded.PartialHash(entry));
        var exF = Assert.Throws<PlaceholderReadException>(() => guarded.FullHash(entry));
        Assert.Equal(0, inner.Calls.GetValueOrDefault(entry.Path));
        Assert.Equal(0, source.OpenCount(entry.Path));
        Assert.Null(exP.InnerException);
    }

    [Fact]
    public void Gate_NormalFile_DelegatesToInner()
    {
        var source = new CountingStreamSource();
        source.Register("tree/a.bin", [1, 2, 3, 4]);
        var inner = CountingHasher.Using(source);
        var guarded = new PlaceholderGuardedHasher(inner);
        var entry = Entry("tree/a.bin", size: 4);

        Assert.Equal("fake-hash", guarded.PartialHash(entry));
        Assert.Equal("fake-hash", guarded.FullHash(entry));
        Assert.Equal(2, inner.Calls["tree/a.bin"]);
        Assert.Equal(2, source.OpenCount("tree/a.bin"));
        Assert.Equal(8, source.BytesRead("tree/a.bin")); // 4 partial + 4 full
    }

    [Fact]
    public void Mutation_GateRemoved_IsDetectedBySpies()
    {
        // Negative test required by the card: a mutation that makes the gate open the
        // placeholder MUST fail. Proves that the spies see the violation: without the
        // decorator, the hasher "succeeds" in opening — and that is exactly what turns red.
        var source = new CountingStreamSource();
        source.Register("tree/p.offline", new byte[4096]);
        var mutant = CountingHasher.Using(source); // without PlaceholderGuardedHasher
        var entry = Entry("tree/p.offline", size: 4096, attrs: FileAttributes.Offline, isPlaceholder: true);

        _ = mutant.PartialHash(entry); // simulated mutation: gate bypass

        Assert.True(mutant.Calls[entry.Path] > 0);     // hash called on placeholder...
        Assert.True(source.OpenCount(entry.Path) > 0);  // ...stream opened...
        Assert.True(source.BytesRead(entry.Path) > 0);  // ...bytes read — violation exposed
    }
}
