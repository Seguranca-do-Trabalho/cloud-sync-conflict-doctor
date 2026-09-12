using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T10 (t_99a01c53) — Hashing BLAKE3 Levels 2/3 (SPEC §8/§9; ADR-0005; test-strategy §3.6).
///
/// Covered IDs:
///   HSH-01 PartialHash_ReadsOnlyFirst64KiBAndLast64KiB — ADR-0005 §3 recipe v1
///          proven with boundary files 131071/131072/131073 bytes and spy stream;
///   HSH-05 empty file → zero-bytes hash without any reading; lowercase hex output;
///   placeholder gate (ADR-0005 §6): PlaceholderReadException, zero bytes read;
///   NEGATIVE mutation test: reversing window order produces DISTINCT hash —
///   order is part of v1 scheme (ADR-0005 §2) and order regression is detectable.
/// </summary>
public class Blake3HasherTests : IDisposable
{
    private const int Kib = 1024;
    private const int WindowBytes = 64 * Kib;        // v1 window (ADR-0005 §3)
    private const int WholeFileLimit = 128 * Kib;    // 131072: <= limit ⇒ read entire file

    private readonly List<string> _tempFiles = new();

    /// <summary>Spy stream: records each read segment as (offset, length).</summary>
    private sealed class SpyStream : Stream
    {
        private readonly byte[] _data;

        public SpyStream(byte[] data) => _data = data;

        public List<(long Offset, int Length)> Reads { get; } = new();

        public long BytesReadTotal { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _data.Length - Position);
            if (n <= 0)
            {
                return 0;
            }

            Reads.Add((Position, n));
            Array.Copy(_data, Position, buffer, offset, n);
            Position += n;
            BytesReadTotal += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => Length + offset,
        };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Runs production path over already-opened spy stream.</summary>
    private static string HashPartialViaSpy(SpyStream spy, long declaredSize) =>
        Blake3Hasher.ComputeHash(spy, declaredSize, partial: true, ct: default);

    // ---- HSH-01: v1 recipe on exact boundaries ---------------------------

    [Theory]
    [InlineData(WholeFileLimit - 1)] // 131071 <= limit: whole file in single pass
    [InlineData(WholeFileLimit)]     // 131072 = limit: whole file in single pass
    [InlineData(WholeFileLimit + 1)] // 131073 > limit: window [0,64KiB) + [size-64KiB,size)
    public void PartialHash_Boundaries128KiB_ExactV1Recipe(long size)
    {
        byte[] content = new byte[size];
        new Random(42).NextBytes(content);

        var spy = new SpyStream(content);
        var got = HashPartialViaSpy(spy, size);

        // Independent expected value, derived directly from ADR-0005 §3 recipe:
        byte[] expectedBytes = size <= WholeFileLimit
            ? content[..(int)size]
            : content[0..WindowBytes].Concat(content[(int)(size - WindowBytes)..]).ToArray();
        var expected = Convert.ToHexString(Blake3.Hasher.Hash(expectedBytes).AsSpan()).ToLowerInvariant();

        Assert.Equal(expected, got);

        if (size <= WholeFileLimit)
        {
            Assert.Single(spy.Reads); // single sequential pass
            Assert.Equal(size, spy.BytesReadTotal);
            Assert.Equal((0L, (int)size), spy.Reads[0]);
        }
        else
        {
            // Two windows, in this order, without overlap or re-reading.
            Assert.Equal(2, spy.Reads.Count);
            Assert.Equal((0L, WindowBytes), spy.Reads[0]);
            Assert.Equal((size - WindowBytes, WindowBytes), spy.Reads[1]);
            Assert.Equal(2 * WindowBytes, spy.BytesReadTotal);
        }
    }

    [Fact]
    public void PartialHash_LargeFile_ReadsOnly128KiB_StartEndPattern()
    {
        var content = new byte[Kib * Kib]; // 1 MiB
        new Random(7).NextBytes(content);

        var spy = new SpyStream(content);
        var got = HashPartialViaSpy(spy, content.Length);

        Assert.True(spy.BytesReadTotal <= 128 * Kib, $"read {spy.BytesReadTotal} bytes");
        Assert.Equal(2, spy.Reads.Count);
        Assert.Equal((0L, WindowBytes), spy.Reads[0]);
        Assert.Equal((content.Length - WindowBytes, WindowBytes), spy.Reads[1]);

        // Value matches hash of two windows concatenated in this order.
        byte[] windows = content[0..WindowBytes].Concat(content[^WindowBytes..]).ToArray();
        var expected = Convert.ToHexString(Blake3.Hasher.Hash(windows).AsSpan()).ToLowerInvariant();
        Assert.Equal(expected, got);
    }

    // ---- HSH-05: empty and lowercase hex --------------------------------------

    [Fact]
    public void PartialHash_EmptyFile_ZeroBytesHash_NoReads()
    {
        var spy = new SpyStream(Array.Empty<byte>());
        var got = HashPartialViaSpy(spy, 0);

        Assert.Empty(spy.Reads); // no reads
        Assert.Equal(Blake3Hasher.EmptyFileHash, got);
    }

    [Fact]
    public void EmptyFileHash_Known_Stable()
    {
        // BLAKE3 of zero bytes (external reference from BLAKE3 spec).
        Assert.Equal("af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262", Blake3Hasher.EmptyFileHash);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hash_OnRealFile_LowercaseHexOutput64Chars(bool partial)
    {
        var path = NewTempFile(4096);
        var hasher = new Blake3Hasher();
        var entry = EntryFor(path);

        var hex = partial ? hasher.PartialHash(entry, CancellationToken.None)
                          : hasher.FullHash(entry, CancellationToken.None);

        Assert.Matches("^[0-9a-f]{64}$", hex);
    }

    // ---- Placeholder gate (ADR-0005 §6; contracts.md IHasher) -------------

    [Fact]
    public void Hash_Placeholder_ThrowsPlaceholderReadException_DoesNotOpenFile()
    {
        var path = NewTempFile(2048);
        var entry = EntryFor(path) with { IsPlaceholder = true };

        var opened = false;
        var hasher = new Blake3Hasher(_ =>
        {
            opened = true;
            throw new InvalidOperationException("placeholder was opened");
        });

        var ex = Assert.Throws<PlaceholderReadException>(
            () => hasher.PartialHash(entry, CancellationToken.None));
        Assert.Equal(path, ex.EntryPath);
        Assert.False(opened); // gate precedes any opening

        var full = Assert.Throws<PlaceholderReadException>(
            () => hasher.FullHash(entry, CancellationToken.None));
        Assert.Equal(path, full.EntryPath);
    }

    // ---- NEGATIVE mutation test on concatenation order ------------------

    [Fact]
    public void MutationWindowOrder_ChangesHash_OrderIsPartOfV1Scheme()
    {
        // If someone reverses the window order (end before start), the hash CHANGES.
        // This locks down the property making boundary tests capable of detecting order regressions.
        var content = new byte[WholeFileLimit + 4096];
        new Random(9).NextBytes(content);

        var startPlusEnd = content[0..WindowBytes].Concat(content[^WindowBytes..]).ToArray();
        var endPlusStart = content[^WindowBytes..].Concat(content[0..WindowBytes]).ToArray();

        var a = Convert.ToHexString(Blake3.Hasher.Hash(startPlusEnd).AsSpan()).ToLowerInvariant();
        var b = Convert.ToHexString(Blake3.Hasher.Hash(endPlusStart).AsSpan()).ToLowerInvariant();

        Assert.NotEqual(a, b);

        // And the real hasher produces start then end:
        var spy = new SpyStream(content);
        Assert.Equal(a, HashPartialViaSpy(spy, content.Length));
    }

    // ---- Full hash: integral read in single pass --------------------------

    [Fact]
    public void FullHash_ReadsEntire_SinglePass_IntegralBLAKE3Value()
    {
        var content = new byte[300 * Kib];
        new Random(11).NextBytes(content);
        var spy = new SpyStream(content);

        var got = Blake3Hasher.ComputeHash(spy, content.Length, partial: false, ct: default);

        // "Single sequential read" (ADR-0005 §3): contiguous pass [0, size),
        // without holes or re-reading — independent of internal buffer size.
        long expectedOffset = 0;
        foreach (var (offset, length) in spy.Reads)
        {
            Assert.Equal(expectedOffset, offset);
            expectedOffset += length;
        }

        Assert.Equal(content.Length, expectedOffset);
        Assert.Equal(content.Length, spy.BytesReadTotal);
        var expected = Convert.ToHexString(Blake3.Hasher.Hash(content).AsSpan()).ToLowerInvariant();
        Assert.Equal(expected, got);
    }

    // ---- Unexpected EOF (TOCTOU: shrank between L0 and read) --------------

    [Fact]
    public void PartialHash_FileSmallerThanDeclared_UnexpectedEOF_DoesNotSilence()
    {
        var real = new byte[1024]; // declared size: 300 KiB
        var spy = new SpyStream(real);

        Assert.ThrowsAny<Exception>(() =>
            Blake3Hasher.ComputeHash(spy, declaredSize: 300 * Kib, partial: true, ct: default));
    }

    // ---- FullHashBlake3 (streaming) ----

    [Fact]
    public void FullHashBlake3_300KiBFile_IntegralHashViaIStreamSource_DiffersFromWindowPartial()
    {
        var content = new byte[300 * Kib];
        new Random(23).NextBytes(content);
        var path = NewTempFileFromBytes(content);
        var entry = EntryFor(path);

        var source = new CountingStreamSource();
        source.Register(path, content);

        var full = Blake3Hasher.FullHashBlake3(source, entry, CancellationToken.None);

        // Independent expected value: BLAKE3 of WHOLE file (ADR-0005 §4).
        var expected = Convert.ToHexString(Blake3.Hasher.Hash(content).AsSpan()).ToLowerInvariant();
        Assert.Equal(expected, full);
        Assert.Matches("^[0-9a-f]{64}$", full);

        // Partial of SAME windows (recipe v1) is a different value — full covers everything.
        var windows = content[0..WindowBytes].Concat(content[^WindowBytes..]).ToArray();
        var partial = Convert.ToHexString(Blake3.Hasher.Hash(windows).AsSpan()).ToLowerInvariant();
        var hasher = new Blake3Hasher();
        Assert.Equal(partial, hasher.PartialHash(entry, CancellationToken.None));
        Assert.NotEqual(partial, full);

        // Streaming via single content source: ONE open, all bytes read,
        // without loading entire file into memory.
        Assert.Equal(1, source.OpenCount(path));
        Assert.Equal(content.Length, source.BytesRead(path));
    }

    [Fact]
    public void PartialCollision_IdenticalWindows_DifferentFull_SeparatesRealConflict()
    {
        // Two files with [0,64KiB) and last 64KiB IDENTICAL and different core:
        // partial (L2) collides — survives together in group — and full (L3)
        // separates as RealConflict, never IdenticalDuplicate (SPEC §9).
        var a = new byte[300 * Kib];
        new Random(31).NextBytes(a);
        var b = (byte[])a.Clone();
        b[150 * Kib] ^= 0xFF; // divergence only in core, outside both windows
        Assert.NotEqual(a[WindowBytes..^WindowBytes], b[WindowBytes..^WindowBytes]);

        var pathA = NewTempFileFromBytes(a);
        var pathB = NewTempFileFromBytes(b);
        var hasher = new Blake3Hasher();

        var partialA = hasher.PartialHash(EntryFor(pathA), CancellationToken.None);
        var partialB = hasher.PartialHash(EntryFor(pathB), CancellationToken.None);
        Assert.Equal(partialA, partialB); // L2 does NOT eliminate either

        var source = new CountingStreamSource();
        source.Register(pathA, a);
        source.Register(pathB, b);
        var fullA = Blake3Hasher.FullHashBlake3(source, EntryFor(pathA), CancellationToken.None);
        var fullB = Blake3Hasher.FullHashBlake3(source, EntryFor(pathB), CancellationToken.None);
        Assert.NotEqual(fullA, fullB); // L3 separates: RealConflict (§9)

        // Determinism: repeating produces exactly the same values.
        Assert.Equal(fullA, Blake3Hasher.FullHashBlake3(source, EntryFor(pathA), CancellationToken.None));
        Assert.Equal(fullB, Blake3Hasher.FullHashBlake3(source, EntryFor(pathB), CancellationToken.None));
    }

    // ---- Helpers --------------------------------------------------------------

    private string NewTempFileFromBytes(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"t99-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private string NewTempFile(long size)
    {
        var path = Path.Combine(Path.GetTempPath(), $"t99-{Guid.NewGuid():N}.bin");
        using (var fs = File.Create(path))
        {
            var buf = new byte[32 * Kib];
            for (long written = 0; written < size; )
            {
                var chunk = (int)Math.Min(buf.Length, size - written);
                for (var i = 0; i < chunk; i++)
                {
                    buf[i] = (byte)((written + i) % 251);
                }

                fs.Write(buf, 0, chunk);
                written += chunk;
            }
        }

        _tempFiles.Add(path);
        return path;
    }

    private FileEntry EntryFor(string path)
    {
        var fi = new FileInfo(path);
        return new FileEntry
        {
            Path = path,
            Size = fi.Length,
            MtimeUtc = fi.LastWriteTimeUtc,
            Attributes = FileAttributes.Normal,
            VolumeId = "v-test",
            FileId = path,
        };
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles.Where(File.Exists))
        {
            File.Delete(f);
        }
    }
}
