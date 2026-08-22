namespace Doctor.Tests;

using System.Security.Cryptography;
using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — PLH-02 (docs/test-strategy.md §3.2): árvore FX-PLACEHOLDER em
/// tmp com placeholders simulados via sidecar `.placeholder-meta.json` (convenção
/// T04). Scan completo; placeholder_bytes_read == 0; conteúdo dos placeholders intacto
/// byte a byte (sha256 antes == depois); Placeholders[] coerente com a enumeração.
/// </summary>
public class PlaceholderPipelineIntegrationTests : IDisposable
{
    private readonly string _raiz;

    public PlaceholderPipelineIntegrationTests()
    {
        _raiz = Path.Combine(Path.GetTempPath(), "cdt09-fx-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_raiz);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_raiz, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Convenção T04: sidecar JSON marca o arquivo como placeholder simulado
    /// com os atributos que só existem no Windows. O arquivo em si permanece um arquivo
    /// regular do FS — o scan NUNCA deve abri-lo.</summary>
    private static void MarcarComoPlaceholder(string caminhoArquivo, string motivo)
    {
        var sidecar = caminhoArquivo + ".placeholder-meta.json";
        File.WriteAllText(sidecar, $"{{\"kind\":\"{motivo}\"}}");
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    [Fact]
    public void ArvoreMista_ScanCompleta_ZeroBytesDePlaceholder_ConteudoIntacto()
    {
        // ---- FX-PLACEHOLDER: normais + 4 motivos da SPEC §21 via sidecar + reparse real
        var docs = Path.Combine(_raiz, "docs");
        var morto = Path.Combine(_raiz, "arquivo morto");
        _ = Directory.CreateDirectory(docs);
        _ = Directory.CreateDirectory(morto);

        var normalA = Path.Combine(docs, "normal-a.txt");
        var normalB = Path.Combine(docs, "normal-b.txt"); // duplicata idêntica de normal-a
        var unico = Path.Combine(_raiz, "unico.dat");
        var offline = Path.Combine(morto, "relatorio antigo.docx");
        var roo = Path.Combine(docs, "contrato.pdf");
        var roda = Path.Combine(docs, "video-aula.mp4");

        var conteudoNormal = "conteudo normal compartilhado pelas duas copias"u8.ToArray();
        File.WriteAllBytes(normalA, conteudoNormal);
        File.WriteAllBytes(normalB, conteudoNormal);
        File.WriteAllBytes(unico, "arquivo sem par"u8.ToArray());

        var offlineBytes = new byte[96 * 1024]; // >0 para o teste provar zero leitura
        RandomNumberGenerator.Fill(offlineBytes);
        File.WriteAllBytes(offline, offlineBytes);
        File.WriteAllBytes(roo, new byte[32 * 1024]);
        File.WriteAllBytes(roda, new byte[160 * 1024]);
        MarcarComoPlaceholder(offline, "offline");
        MarcarComoPlaceholder(roo, "recall_on_open");
        MarcarComoPlaceholder(roda, "recall_on_data_access");

        // Reparse REAL (symlink de arquivo) — marcado pelo FS, sem sidecar.
        File.CreateSymbolicLink(Path.Combine(_raiz, "atalho.txt"), normalA);

        // Hashes ANTES do scan (prova de não-destruição byte a byte).
        var hashOfflineAntes = Sha256(offline);
        var hashRooAntes = Sha256(roo);
        var hashRodaAntes = Sha256(roda);

        // ---- SCAN (pipeline de enumeradores: físico → ordenado → convenção T04)
        var resultado = new SidecarPlaceholderEnumerator(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()))
            .Enumerate(_raiz, CancellationToken.None);

        var arquivos = resultado.Files;
        var porCaminho = arquivos.ToDictionary(f => f.Path, f => f);

        // Sidecar marca os três; symlink é marcado pela origem.
        Assert.True(porCaminho[offline].IsPlaceholder);
        Assert.True(porCaminho[roo].IsPlaceholder);
        Assert.True(porCaminho[roda].IsPlaceholder);
        Assert.True(porCaminho[Path.Combine(_raiz, "atalho.txt")].IsPlaceholder);

        // Normais continuam normais.
        Assert.False(porCaminho[normalA].IsPlaceholder);
        Assert.False(porCaminho[unico].IsPlaceholder);

        // ---- Conteúdo dos placeholders intacto byte a byte
        Assert.Equal(hashOfflineAntes, Sha256(offline));
        Assert.Equal(hashRooAntes, Sha256(roo));
        Assert.Equal(hashRodaAntes, Sha256(roda));

        // ---- Telemetria: contagem e invariante absoluta (SPEC §21)
        Assert.Equal(arquivos.Count, resultado.Telemetry.FilesEnumerated);
        Assert.Equal(4, resultado.Telemetry.FilesPlaceholder);
        Assert.Equal(0, resultado.Telemetry.PlaceholderBytesRead);

        // ---- Placeholders[] conforme schema v1 §6.3 (ordem por bytes de caminho)
        var registros = PlaceholderReport.Records(arquivos);
        Assert.Equal(4, registros.Count);
        Assert.Equal(
            registros.Select(r => r.Path).ToArray(),
            registros.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal("reparse_point", registros.Single(r => r.Path.EndsWith("atalho.txt", StringComparison.Ordinal)).Kinds.Single());
        Assert.Equal("offline", registros.Single(r => r.Path == offline).Kinds.Single());
        Assert.Equal("recall_on_open", registros.Single(r => r.Path == roo).Kinds.Single());
        Assert.Equal("recall_on_data_access", registros.Single(r => r.Path == roda).Kinds.Single());

        // ---- Gate no hasher: nenhum placeholder chega ao conteúdo (PLH-01 na prática).
        // Qualquer chamada sobre placeholder explode; normais hasham normalmente.
        var fonte = new CountingStreamSource();
        foreach (var f in arquivos.Where(f => !f.IsPlaceholder))
        {
            fonte.Register(f.Path, File.ReadAllBytes(f.Path));
        }

        var hasher = new PlaceholderGuardedHasher(CountingHasher.Using(fonte));

        foreach (var placeholder in arquivos.Where(f => f.IsPlaceholder))
        {
            Assert.Throws<PlaceholderReadException>(() => hasher.PartialHash(placeholder));
            Assert.Throws<PlaceholderReadException>(() => hasher.FullHash(placeholder));
        }

        _ = hasher.PartialHash(porCaminho[normalA]); // normais passam pelo gate

        // Nenhum stream foi aberto sobre qualquer placeholder (dupla evidência PLH-01).
        foreach (var placeholder in arquivos.Where(f => f.IsPlaceholder))
        {
            Assert.Equal(0, fonte.OpenCount(placeholder.Path));
            Assert.Equal(0, fonte.BytesRead(placeholder.Path));
        }
    }

    [Fact]
    public void ScanDeArvoreSoComPlaceholders_NaoLeNada_EListaTodos()
    {
        var p1 = Path.Combine(_raiz, "so-offline.bin");
        var p2 = Path.Combine(_raiz, "so-roda.bin");
        File.WriteAllBytes(p1, new byte[4096]);
        File.WriteAllBytes(p2, new byte[8192]);
        MarcarComoPlaceholder(p1, "offline");
        MarcarComoPlaceholder(p2, "recall_on_data_access");

        var resultado = new SidecarPlaceholderEnumerator(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()))
            .Enumerate(_raiz, CancellationToken.None);

        Assert.Equal(2, resultado.Telemetry.FilesEnumerated); // sidecars não contam
        Assert.Equal(2, resultado.Telemetry.FilesPlaceholder);
        Assert.Equal(0, resultado.Telemetry.FilesSkipped);
        Assert.Equal(0, resultado.Telemetry.FilesPartialHashed);
        Assert.Equal(0, resultado.Telemetry.PlaceholderBytesRead);
        Assert.Equal(2, PlaceholderReport.Records(resultado.Files).Count);
    }
}
