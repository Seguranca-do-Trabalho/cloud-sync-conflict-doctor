namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — espião de abertura de stream (PLH-01): registra toda abertura
/// e todo byte lido, por caminho. Prova "zero streams abertos sobre placeholder".
/// NÃO é código de produto.
/// </summary>
public sealed class CountingStreamSource : IStreamSource
{
    private readonly Dictionary<string, byte[]> _conteudo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _aberturas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _bytesLidos = new(StringComparer.Ordinal);

    public void Register(string path, byte[] bytes) => _conteudo[path] = bytes;

    public Stream OpenRead(FileEntry entry)
    {
        if (!_conteudo.TryGetValue(entry.Path, out var bytes))
        {
            throw new FileNotFoundException("CountingStreamSource: conteudo nao registrado", entry.Path);
        }

        _aberturas[entry.Path] = _aberturas.GetValueOrDefault(entry.Path) + 1;
        return new ContadorStream(this, entry.Path, bytes);
    }

    public int OpenCount(string path) => _aberturas.GetValueOrDefault(path);

    public long BytesRead(string path) => _bytesLidos.GetValueOrDefault(path);

    public long TotalBytes => _bytesLidos.Values.Sum();

    private sealed class ContadorStream : MemoryStream
    {
        private readonly CountingStreamSource _dono;
        private readonly string _path;

        public ContadorStream(CountingStreamSource dono, string path, byte[] bytes)
            : base(bytes, writable: false)
        {
            _dono = dono;
            _path = path;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            _dono._bytesLidos[_path] = _dono._bytesLidos.GetValueOrDefault(_path) + read;
            return read;
        }
    }
}

/// <summary>
/// T09 (t_349dc2c0) — hasher espio (PLH-01/02): conta chamadas por caminho e le
/// exclusivamente via <see cref="IStreamSource"/> para que "nenhuma funcao de hash
/// chamada sobre placeholder" e "nenhum stream aberto sobre placeholder" sejam
/// observaveis. NÃO é código de produto.
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
        return Drenar(_streams.OpenRead(entry));
    }

    public string FullHash(FileEntry entry, CancellationToken ct = default)
    {
        Calls[entry.Path] = Calls.GetValueOrDefault(entry.Path) + 1;
        return Drenar(_streams.OpenRead(entry));
    }

    private static string Drenar(Stream stream)
    {
        using var s = stream;
        var buffer = new byte[4096];
        while (s.Read(buffer, 0, buffer.Length) > 0)
        {
        }

        return "fake-hash";
    }
}
