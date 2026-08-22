using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T07 — Scan Level 0 sobre enumerador físico fake (ordem arbitrária):
/// (1) toda entrada com atributo placeholder sai marcada IsPlaceholder;
/// (2) NENHUM byte é lido durante a enumeração — provado pelo contador
///     de ReadBytes no próprio enumerador fake;
/// (3) determinismo: ordens físicas de inserção distintas produzem a mesma lista ordenada.
/// </summary>
public class OrderedFileEnumeratorTests
{
    /// <summary>
    /// Simula o filesystem físico: entrega entradas em ordem arbitrária e conta acessos
    /// a conteúdo. Se qualquer etapa da enumeração ordenada tentar ler bytes, o contador sobe.
    /// </summary>
    private sealed class FakePhysicalEnumerator : IFileEnumerator
    {
        private readonly FileEntry[] _physical;

        public int ReadBytesCallCount { get; private set; }

        public FakePhysicalEnumerator(params FileEntry[] physical) => _physical = physical;

        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
            => new EnumerationResult(
                Files: _physical.ToArray(),
                Errors: Array.Empty<ScanError>(),
                Telemetry: new ScanTelemetry());

        /// <summary>Método que representaria leitura de conteúdo — a enumeração NUNCA pode chamar.</summary>
        public byte[] ReadBytes(FileEntry entry)
        {
            ReadBytesCallCount++;
            return Array.Empty<byte>();
        }
    }

    private static FileEntry Entry(string path, long size = 1, FileAttributes attrs = FileAttributes.Normal)
        => new()
        {
            Path = path,
            Size = size,
            MtimeUtc = DateTimeOffset.UnixEpoch,
            Attributes = attrs,
            VolumeId = "vol-1",
            FileId = "id-" + path,
        };

    [Fact]
    public void Scan_MarcaTodosPlaceholders_E_NuncaLeConteudo()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/normal.txt", 100),
            Entry("/root/onedrive.docx", 200, FileAttributes.Offline | FileAttributes.ReadOnly),
            Entry("/root/recall_open.pdf", 300, (FileAttributes)0x00040000),
            Entry("/root/cloud_only.mov", 400, (FileAttributes)0x00400000),
            Entry("/root/link.dat", 500, FileAttributes.ReparsePoint));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        Assert.Equal(5, result.Files.Count);
        Assert.All(result.Files, e =>
            Assert.Equal(e.Attributes != FileAttributes.Normal, e.IsPlaceholder));
        Assert.Equal(4, result.Files.Count(e => e.IsPlaceholder));
        Assert.Equal(0, result.Telemetry.PlaceholderBytesRead);
        // Prova central do card: zero leitura de conteúdo na enumeração Level 0.
        Assert.Equal(0, fake.ReadBytesCallCount);
    }

    [Fact]
    public void Scan_OrdensFisicasDistintas_SaidaIdentica()
    {
        FileEntry[] entries =
        [
            Entry("/root/a.txt", 10),
            Entry("/root/B.txt", 20),
            Entry("/root/_c.txt", 30),
            Entry("/root/á.txt", 40),
        ];

        var run1 = new OrderedFileEnumerator(new FakePhysicalEnumerator(entries)).Enumerate("/root", CancellationToken.None);
        var run2 = new OrderedFileEnumerator(new FakePhysicalEnumerator(entries.Reverse().ToArray())).Enumerate("/root", CancellationToken.None);
        var run3 = new OrderedFileEnumerator(new FakePhysicalEnumerator(entries.OrderBy(_ => Guid.NewGuid()).ToArray())).Enumerate("/root", CancellationToken.None);

        Assert.Equal(run1.Files.Select(e => e.Path), run2.Files.Select(e => e.Path));
        Assert.Equal(run1.Files.Select(e => e.Path), run3.Files.Select(e => e.Path));
        Assert.Equal(run1.Files, run2.Files); // igualdade completa record-a-record
    }

    [Fact]
    public void Scan_SaidaOrdenadaByteAByte()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/z.txt"),
            Entry("/root/A.txt"),
            Entry("/root/m.txt"));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        var paths = result.Files.Select(e => e.Path).ToArray();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal).ToArray(), paths);
    }
}
