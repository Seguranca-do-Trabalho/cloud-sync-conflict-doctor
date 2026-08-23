namespace Doctor.Tests;

using System.Text.Json;
using Doctor.Core;

/// <summary>
/// T12 (t_d70d59f9) — DET-03 mínimo exigido pelo orquestrador: árvore temporária com
/// 2 duplicatas + 1 conflito real, 3 execuções do pipeline com enumeradores fake em
/// ordens físicas distintas (direta, reversa, embaralhada com seed fixa) ⇒ saída
/// idêntica byte a byte. A árvore usa arquivos > 128 KiB com janelas parciais
/// idênticas e miolos distintos para que o conflito real EXIJA o Level 3 (full hash)
/// — a prova cobre L0→L3 inteiro, não só o agrupamento.
/// </summary>
public sealed class ScanPipelineTests : IDisposable
{
    private const int Kib = 1024;
    private const int ArquivoGrande = 200 * Kib; // > 128 KiB: parcial = janela início+fim
    private const int Janela = 64 * Kib;

    private readonly string _root;

    public ScanPipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"t12-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "backup"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // limpeza best-effort: tmp do sistema operacional recolhe depois
        }
    }

    [Fact]
    public void Scan_TresOrdensDeEnumeracao_SaidaIdenticaByteAByte()
    {
        // ---- árvore: par idêntico (foto.jpg) + par conflito (orcamento) -----------
        var conteudoA = ConteudoPadronizado(semente: 0xA0);
        var parIdêntico1 = CriarArquivo("docs/foto.jpg", conteudoA);
        var parIdêntico2 = CriarArquivo("docs/backup/foto.jpg", conteudoA);

        // Conflito real: MESMO size, MESMAS janelas [0,64K)+[fim-64K,fim), miolos
        // diferentes ⇒ parcial colide (sobrevive ao L2) e o full hash diverge (L3).
        var conflito1 = CriarArquivo("docs/orcamento.xlsx", ConteudoConflito(miolo: 0x11));
        var conflito2 = CriarArquivo("docs/orcamento-DESKTOP-ABC123.xlsx", ConteudoConflito(miolo: 0x22));

        var entradas = new[]
        {
            Entrada(parIdêntico1),
            Entrada(parIdêntico2),
            Entrada(conflito1),
            Entrada(conflito2),
        };

        // ---- três ordens físicas distintas, todas sobre a MESMA árvore ------------
        var direta = entradas.ToArray();
        var reversa = entradas.Reverse().ToArray();
        var embaralhada = Shuffle(entradas, seed: 42);

        var hasher = new Blake3Hasher();
        var bytesDireta = Serializar(RunPipeline(direta, hasher));
        var bytesReversa = Serializar(RunPipeline(reversa, hasher));
        var bytesEmbaralhada = Serializar(RunPipeline(embaralhada, hasher));

        Assert.Equal(bytesDireta, bytesReversa);
        Assert.Equal(bytesDireta, bytesEmbaralhada);

        // ---- correção do veredito (não só determinismo) ---------------------------
        var resultado = RunPipeline(direta, hasher);

        Assert.Equal(2, resultado.Groups.Count);
        Assert.Single(resultado.IdenticalDuplicates);
        Assert.Single(resultado.RealConflicts);

        var duplicata = resultado.IdenticalDuplicates[0];
        Assert.Equal(2, duplicata.Files.Count);
        Assert.Equal(64, duplicata.Hash.Length); // BLAKE3 hex minúscula (ADR-0005 §1)
        Assert.Equal(ArquivoGrande, duplicata.SizeBytes);
        Assert.Equal(parIdêntico2, duplicata.Files[0].Path); // ordem canônica: backup/ < foto
        Assert.Equal(parIdêntico1, duplicata.Files[1].Path);

        var conflito = resultado.RealConflicts[0];
        Assert.Equal("orcamento.xlsx", conflito.NormalizedBaseName);
        Assert.Equal(2, conflito.Files.Count);
        Assert.NotEqual(conflito.Files[0].Hash, conflito.Files[1].Hash); // divergência real
        Assert.Equal(conflito2, conflito.Files[0].Path); // '-' (0x2D) < '.' (0x2E) em bytes
        Assert.Equal(conflito1, conflito.Files[1].Path);
    }

    // ---- construção da árvore ------------------------------------------------------

    private string CriarArquivo(string relativo, byte[] conteudo)
    {
        var caminho = Path.Combine(new[] { _root }.Concat(relativo.Split('/')).ToArray());
        File.WriteAllBytes(caminho, conteudo);
        return caminho;
    }

    private static FileEntry Entrada(string caminho)
    {
        var info = new FileInfo(caminho);
        return new FileEntry
        {
            Path = caminho,
            Size = info.Length,
            MtimeUtc = info.LastWriteTimeUtc,
            Attributes = FileAttributes.Normal,
            VolumeId = "t12-volume",
            FileId = caminho,
        };
    }

    private static byte[] ConteudoPadronizado(byte semente)
    {
        var bytes = new byte[ArquivoGrande];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(semente + (i % 251));
        }

        return bytes;
    }

    /// <summary>Cabeça e cauda fixas (colisão parcial garantida); miolo varia por semente.</summary>
    private static byte[] ConteudoConflito(byte miolo)
    {
        var bytes = new byte[ArquivoGrande];

        for (var i = 0; i < Janela; i++)
        {
            bytes[i] = (byte)(0xAA + (i % 13));
        }

        for (var i = ArquivoGrande - Janela; i < ArquivoGrande; i++)
        {
            bytes[i] = (byte)(0xBB + (i % 17));
        }

        for (var i = Janela; i < ArquivoGrande - Janela; i++)
        {
            bytes[i] = miolo;
        }

        return bytes;
    }

    private static FileEntry[] Shuffle(FileEntry[] original, int seed)
    {
        var copia = original.ToArray();
        var random = new Random(seed);

        for (var i = copia.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copia[i], copia[j]) = (copia[j], copia[i]);
        }

        return copia;
    }

    // ---- pipeline + serialização determinística ------------------------------------

    private ScanResult RunPipeline(FileEntry[] ordemFisica, IHasher hasher)
    {
        var pipeline = new ScanPipeline(new FakeFileEnumerator(ordemFisica), hasher);
        return pipeline.Run(_root);
    }

    /// <summary>
    /// Serialização canônica do resultado para comparação byte a byte: as listas já
    /// saem em ordem canônica do pipeline; a ordem das propriedades JSON é a ordem de
    /// declaração dos records (System.Text.Json), fixa e invariante à cultura.
    /// </summary>
    private static byte[] Serializar(ScanResult resultado) =>
        JsonSerializer.SerializeToUtf8Bytes(resultado, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>
    /// Enumerador fake (L0): devolve as entradas EXATAMENTE na ordem física pedida —
    /// direta, reversa ou embaralhada. Não é o OrderedFileEnumerator: a ordenação
    /// canônica é responsabilidade do pipeline, e é isso que a prova verifica.
    /// </summary>
    private sealed class FakeFileEnumerator : IFileEnumerator
    {
        private readonly IReadOnlyList<FileEntry> _ordem;

        public FakeFileEnumerator(IReadOnlyList<FileEntry> ordem) => _ordem = ordem;

        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) =>
            new(_ordem, Array.Empty<ScanError>(), new ScanTelemetry());
    }
}
