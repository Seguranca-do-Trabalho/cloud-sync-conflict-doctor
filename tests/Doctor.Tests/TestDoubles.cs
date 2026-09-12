namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — stream open spy (PLH-01): records every open
/// and every byte read, per path. Proves "zero streams opened over placeholder".
/// NOT production code.
/// </summary>
public sealed class CountingStreamSource : IStreamSource
{
    private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _opens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _bytesRead = new(StringComparer.Ordinal);

    public void Register(string path, byte[] bytes) => _content[path] = bytes;

    public Stream OpenRead(FileEntry entry)
    {
        if (!_content.TryGetValue(entry.Path, out var bytes))
        {
            throw new FileNotFoundException("CountingStreamSource: content not registered", entry.Path);
        }

        _opens[entry.Path] = _opens.GetValueOrDefault(entry.Path) + 1;
        return new CountingStream(this, entry.Path, bytes);
    }

    public int OpenCount(string path) => _opens.GetValueOrDefault(path);

    public long BytesRead(string path) => _bytesRead.GetValueOrDefault(path);

    public long TotalBytes => _bytesRead.Values.Sum();

    private sealed class CountingStream : MemoryStream
    {
        private readonly CountingStreamSource _owner;
        private readonly string _path;

        public CountingStream(CountingStreamSource owner, string path, byte[] bytes)
            : base(bytes, writable: false)
        {
            _owner = owner;
            _path = path;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            _owner._bytesRead[_path] = _owner._bytesRead.GetValueOrDefault(_path) + read;
            return read;
        }
    }
}

/// <summary>
/// T09 (t_349dc2c0) — spy hasher (PLH-01/02): counts calls per path and reads
/// exclusively via <see cref="IStreamSource"/> so that "no hash function called
/// over placeholder" and "no stream opened over placeholder" are observable.
/// NOT production code.
/// </summary>
public sealed class CountingHasher : IHasher
{
    private readonly IStreamSource _streams;

    private CountingHasher(IStreamSource streams) => _streams = streams;

    public static CountingHasher Using(IStreamSource streams) => new(streams);

    public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

    public string PartialHash(FileEntry entry, CancellationToken ct = default)
    {
        Calls[entry.Path] = Calls.GetValueOrDefault(entry.Path) + 1;
        return Drain(_streams.OpenRead(entry));
    }

    public string FullHash(FileEntry entry, CancellationToken ct = default)
    {
        Calls[entry.Path] = Calls.GetValueOrDefault(entry.Path) + 1;
        return Drain(_streams.OpenRead(entry));
    }

    private static string Drain(Stream stream)
    {
        using var s = stream;
        var buffer = new byte[4096];
        while (s.Read(buffer, 0, buffer.Length) > 0)
        {
        }

        return "fake-hash";
    }
}
