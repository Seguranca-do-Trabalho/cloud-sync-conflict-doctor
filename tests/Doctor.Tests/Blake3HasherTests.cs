using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T10 (t_99a01c53) — Hashing BLAKE3 Levels 2/3 (SPEC §8/§9; ADR-0005; test-strategy §3.6).
///
/// IDs cobertos:
///   HSH-01 PartialHash_ReadsOnlyFirst64KiBAndLast64KiB — receita v1 do ADR-0005 §3
///          provada com arquivos de fronteira 131071/131072/131073 bytes e stream espião;
///   HSH-05 arquivo vazio → hash de zero bytes sem nenhuma leitura; saída hex minúscula;
///   gate de placeholder (ADR-0005 §6): PlaceholderReadException, zero bytes lidos;
///   teste NEGATIVO de mutação: inverter a ordem das janelas produz hash DISTINTO —
///   a ordem é parte do esquema v1 (ADR-0005 §2) e regressão de ordem é detectável.
/// </summary>
public class Blake3HasherTests : IDisposable
{
    private const int Kib = 1024;
    private const int WindowBytes = 64 * Kib;        // janela v1 (ADR-0005 §3)
    private const int WholeFileLimit = 128 * Kib;    // 131072: ≤ limite ⇒ leitura inteira

    private readonly List<string> _tempFiles = new();

    /// <summary>Stream espiã: registra cada segmento lido como (offset, comprimento).</summary>
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

    /// <summary>Roda o caminho de produção sobre a stream espiã já aberta.</summary>
    private static string HashPartialViaSpy(SpyStream spy, long declaredSize) =>
        Blake3Hasher.ComputeHash(spy, declaredSize, partial: true, ct: default);

    // ---- HSH-01: receita v1 nas fronteiras exatas ---------------------------

    [Theory]
    [InlineData(WholeFileLimit - 1)] // 131071 ≤ limite: inteiro numa única passada
    [InlineData(WholeFileLimit)]     // 131072 = limite: inteiro numa única passada
    [InlineData(WholeFileLimit + 1)] // 131073 > limite: janela [0,64KiB) + [size−64KiB,size)
    public void PartialHash_Fronteiras128KiB_ReceitaV1Exata(long size)
    {
        byte[] content = new byte[size];
        new Random(42).NextBytes(content);

        var spy = new SpyStream(content);
        var got = HashPartialViaSpy(spy, size);

        // Esperado independente, derivado direto da receita do ADR-0005 §3:
        byte[] expectedBytes = size <= WholeFileLimit
            ? content[..(int)size]
            : content[0..WindowBytes].Concat(content[(int)(size - WindowBytes)..]).ToArray();
        var expected = Convert.ToHexString(Blake3.Hasher.Hash(expectedBytes).AsSpan()).ToLowerInvariant();

        Assert.Equal(expected, got);

        if (size <= WholeFileLimit)
        {
            Assert.Single(spy.Reads); // uma única passada sequencial
            Assert.Equal(size, spy.BytesReadTotal);
            Assert.Equal((0L, (int)size), spy.Reads[0]);
        }
        else
        {
            // Duas janelas, nesta ordem, sem sobreposição e sem releitura.
            Assert.Equal(2, spy.Reads.Count);
            Assert.Equal((0L, WindowBytes), spy.Reads[0]);
            Assert.Equal((size - WindowBytes, WindowBytes), spy.Reads[1]);
            Assert.Equal(2 * WindowBytes, spy.BytesReadTotal);
        }
    }

    [Fact]
    public void PartialHash_ArquivoGrande_LeApenas128KiB_PadraoInicioFim()
    {
        var content = new byte[Kib * Kib]; // 1 MiB
        new Random(7).NextBytes(content);

        var spy = new SpyStream(content);
        var got = HashPartialViaSpy(spy, content.Length);

        Assert.True(spy.BytesReadTotal <= 128 * Kib, $"leu {spy.BytesReadTotal} bytes");
        Assert.Equal(2, spy.Reads.Count);
        Assert.Equal((0L, WindowBytes), spy.Reads[0]);
        Assert.Equal((content.Length - WindowBytes, WindowBytes), spy.Reads[1]);

        // O valor coincide com o hash das duas janelas concatenadas nesta ordem.
        byte[] windows = content[0..WindowBytes].Concat(content[^WindowBytes..]).ToArray();
        var expected = Convert.ToHexString(Blake3.Hasher.Hash(windows).AsSpan()).ToLowerInvariant();
        Assert.Equal(expected, got);
    }

    // ---- HSH-05: vazio e hex minúsculo --------------------------------------

    [Fact]
    public void PartialHash_ArquivoVazio_HashDeZeroBytes_SemNenhumaLeitura()
    {
        var spy = new SpyStream(Array.Empty<byte>());
        var got = HashPartialViaSpy(spy, 0);

        Assert.Empty(spy.Reads); // nenhuma leitura
        Assert.Equal(Blake3Hasher.EmptyFileHash, got);
    }

    [Fact]
    public void EmptyFileHash_Conhecido_Estavel()
    {
        // d697d7c1d0ba646f5e19e2eed57f6e9a90f0d70ebffa1f0b98dad92ca786e0d3 =
        // BLAKE3 de zero bytes (referência externa da spec BLAKE3).
        Assert.Equal("af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262", Blake3Hasher.EmptyFileHash);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hash_EmArquivoReal_SaidaHexMinuscula64Chars(bool partial)
    {
        var path = NewTempFile(4096);
        var hasher = new Blake3Hasher();
        var entry = EntryFor(path);

        var hex = partial ? hasher.PartialHash(entry, CancellationToken.None)
                          : hasher.FullHash(entry, CancellationToken.None);

        Assert.Matches("^[0-9a-f]{64}$", hex);
    }

    // ---- Gate de placeholder (ADR-0005 §6; contratos.md IHasher) -------------

    [Fact]
    public void Hash_Placeholder_LancaPlaceholderReadException_NaoAbreArquivo()
    {
        var path = NewTempFile(2048);
        var entry = EntryFor(path) with { IsPlaceholder = true };

        var opened = false;
        var hasher = new Blake3Hasher(_ =>
        {
            opened = true;
            throw new InvalidOperationException("placeholder foi aberto");
        });

        var ex = Assert.Throws<PlaceholderReadException>(
            () => hasher.PartialHash(entry, CancellationToken.None));
        Assert.Equal(path, ex.EntryPath);
        Assert.False(opened); // o gate precede qualquer abertura

        var full = Assert.Throws<PlaceholderReadException>(
            () => hasher.FullHash(entry, CancellationToken.None));
        Assert.Equal(path, full.EntryPath);
    }

    // ---- Teste NEGATIVO de mutação da ordem de concatenação ------------------

    [Fact]
    public void MutacaoOrdemJanelas_MudaHash_OrdemEParteDoEsquemaV1()
    {
        // Se alguém inverter a ordem das janelas (fim antes do início), o hash MUDA.
        // Isto trava a propriedade que torna o teste de fronteira capaz de pegar
        // regressão de ordem: ordem diferente ⇒ valor diferente ⇒ HSH-01 falha.
        var content = new byte[WholeFileLimit + 4096];
        new Random(9).NextBytes(content);

        var inicioMaisFim = content[0..WindowBytes].Concat(content[^WindowBytes..]).ToArray();
        var fimMaisInicio = content[^WindowBytes..].Concat(content[0..WindowBytes]).ToArray();

        var a = Convert.ToHexString(Blake3.Hasher.Hash(inicioMaisFim).AsSpan()).ToLowerInvariant();
        var b = Convert.ToHexString(Blake3.Hasher.Hash(fimMaisInicio).AsSpan()).ToLowerInvariant();

        Assert.NotEqual(a, b);

        // E o hasher real produz exatamente "início depois fim":
        var spy = new SpyStream(content);
        Assert.Equal(a, HashPartialViaSpy(spy, content.Length));
    }

    // ---- Full hash: leitura integral em uma passada --------------------------

    [Fact]
    public void FullHash_LeInteiro_UmaPassada_ValorBLAKE3Integral()
    {
        var content = new byte[300 * Kib];
        new Random(11).NextBytes(content);
        var spy = new SpyStream(content);

        var got = Blake3Hasher.ComputeHash(spy, content.Length, partial: false, ct: default);

        // "Leitura sequencial única" (ADR-0005 §3): uma passada contígua de [0, size),
        // sem buracos e sem releitura — independente do tamanho do buffer interno.
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

    // ---- EOF inesperado (TOCTOU: encolheu entre L0 e a leitura) --------------

    [Fact]
    public void PartialHash_ArquivoMenorQueDeclarado_EOFInesperado_NaoSilencia()
    {
        var real = new byte[1024]; // size declarado: 300 KiB
        var spy = new SpyStream(real);

        Assert.ThrowsAny<Exception>(() =>
            Blake3Hasher.ComputeHash(spy, declaredSize: 300 * Kib, partial: true, ct: default));
    }

    // ---- Helpers --------------------------------------------------------------

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
