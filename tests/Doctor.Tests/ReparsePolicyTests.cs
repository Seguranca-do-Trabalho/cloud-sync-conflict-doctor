namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T18 (t_c894ff83) — S11-2: política de reparse no enumerador ordenado (SPEC §6,
/// threat-model T-02, ADR-0004 regra 4). Defesa em profundidade ACIMA do enumerador
/// físico: <see cref="OrderedFileEnumerator"/> aplica <see cref="ReparsePolicy"/> —
/// (1) marca toda entrada com <see cref="FileEntry.IsReparsePoint"/>;
/// (2) rejeita caminhos que voltam ao mesmo inode (guarda de visitados por
///     (VolumeId, FileId) — mitigação obrigatória do T-02);
/// (3) recusa entradas além do teto de profundidade (<see cref="ReparsePolicy.MaxDepth"/> = 16),
///     registrando cada rejeição em <see cref="EnumerationResult.Errors"/> — sem falha silenciosa
///     (contratos.md R10).
/// </summary>
public class ReparsePolicyTests : IDisposable
{
    private readonly string _raiz;

    public ReparsePolicyTests()
    {
        _raiz = Path.Combine(Path.GetTempPath(), "cdt18-reparse-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Físico simulado: entrega entradas em ordem arbitrária e conta leituras de conteúdo.</summary>
    private sealed class FakePhysicalEnumerator : IFileEnumerator
    {
        private readonly FileEntry[] _physical;
        public FakePhysicalEnumerator(params FileEntry[] physical) => _physical = physical;
        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
            => new(_physical.ToArray(), Array.Empty<ScanError>(), new ScanTelemetry());

        /// <summary>Representa leitura de conteúdo — a enumeração NUNCA pode chamar.</summary>
        public int ReadBytesCallCount { get; private set; }
        public byte[] ReadBytes(FileEntry entry) { ReadBytesCallCount++; return Array.Empty<byte>(); }
    }

    private static FileEntry Entry(
        string path,
        long size = 1,
        FileAttributes attrs = FileAttributes.Normal,
        string volumeId = "vol-1",
        string? fileId = null)
        => new()
        {
            Path = path,
            Size = size,
            MtimeUtc = DateTimeOffset.UnixEpoch,
            Attributes = attrs,
            VolumeId = volumeId,
            FileId = fileId ?? "id-" + path,
        };

    // ------------------------------------------------------------------
    // 8 casos de REPARSE
    // ------------------------------------------------------------------

    [Fact]
    public void Reparse01_ArquivoComBitReparse_SaiMarcadoIsReparsePoint()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/link.dat", attrs: FileAttributes.ReparsePoint));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        var entrada = Assert.Single(result.Files);
        Assert.True(entrada.IsReparsePoint);
        Assert.True(entrada.IsPlaceholder);
        Assert.Equal(PlaceholderKind.ReparsePoint, entrada.PlaceholderKind);
    }

    [Fact]
    public void Reparse02_ArquivoNormal_NaoEMarcadoReparse()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/normal.txt"),
            Entry("/root/offline.docx", attrs: FileAttributes.Offline));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        Assert.All(result.Files, e => Assert.False(e.IsReparsePoint));
        var offline = result.Files.Single(f => f.Path.EndsWith("offline.docx", StringComparison.Ordinal));
        Assert.True(offline.IsPlaceholder);   // offline segue placeholder...
        Assert.False(offline.IsReparsePoint); // ...mas NÃO é reparse
    }

    [Fact]
    public void Reparse03_MarcaDaOrigem_NuncaEApagadaPelaProjecao()
    {
        // Entrada que JÁ chega marcada IsReparsePoint=true do enumerador físico
        // (autoridade da origem, mesma regra da falha fechada de PlaceholderPolicy).
        var original = Entry("/root/vinculo.txt") with { IsReparsePoint = true };
        var fake = new FakePhysicalEnumerator(original);

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        Assert.True(Assert.Single(result.Files).IsReparsePoint);
    }

    [Fact]
    public void Reparse04_IntegracaoSymlinkDeArquivo_EntraMarcadoNaListaOrdenada()
    {
        File.WriteAllText(Path.Combine(_raiz, "real.txt"), "conteudo");
        File.CreateSymbolicLink(Path.Combine(_raiz, "atalho.txt"), Path.Combine(_raiz, "real.txt"));

        var result = new OrderedFileEnumerator(new CrossPlatformEnumerator())
            .Enumerate(_raiz, CancellationToken.None);

        var atalho = Assert.Single(result.Files, f => f.Path.EndsWith("atalho.txt", StringComparison.Ordinal));
        Assert.True(atalho.IsReparsePoint);
        Assert.Equal(PlaceholderKind.ReparsePoint, atalho.PlaceholderKind);
    }

    [Fact]
    public void Reparse05_IntegracaoJunctionDeDiretorio_EhFolhaEOScanContinua()
    {
        _ = Directory.CreateDirectory(Path.Combine(_raiz, "alvo"));
        File.WriteAllText(Path.Combine(_raiz, "alvo", "dentro.txt"), "x");
        File.WriteAllText(Path.Combine(_raiz, "antes.txt"), "a");
        File.CreateSymbolicLink(Path.Combine(_raiz, "juncao"), Path.Combine(_raiz, "alvo"));

        var result = new OrderedFileEnumerator(new CrossPlatformEnumerator())
            .Enumerate(_raiz, CancellationToken.None);

        Assert.Contains(result.Files, f => f.Path.EndsWith("antes.txt", StringComparison.Ordinal));
        Assert.Contains(result.Files, f => f.Path.EndsWith("dentro.txt", StringComparison.Ordinal));
        // juncao é folha registrada — nunca silenciosa, nunca atravessada como diretório.
        Assert.Contains(result.Errors, e => e.Path.EndsWith("juncao", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files, f => f.Path.Contains("juncao", StringComparison.Ordinal));
    }

    [Fact]
    public void Reparse06_SymlinkParaForaDaRaiz_ConteudoExternoNaoVaza()
    {
        var fora = Path.Combine(Path.GetTempPath(), "cdt18-fora-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(fora);
        File.WriteAllText(Path.Combine(fora, "segredo.txt"), "fora");
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "sub"));
            File.CreateSymbolicLink(Path.Combine(_raiz, "sub", "escape"), fora);

            var result = new OrderedFileEnumerator(new CrossPlatformEnumerator())
                .Enumerate(_raiz, CancellationToken.None);

            Assert.DoesNotContain(result.Files, f => f.Path.Contains("segredo", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.Path.EndsWith("escape", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fora, recursive: true);
        }
    }

    [Fact]
    public void Reparse07_TelemetriaCoerente_ComEntradasReparse()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/b.dat", attrs: FileAttributes.ReparsePoint),
            Entry("/root/a.txt"),
            Entry("/root/c.link", attrs: FileAttributes.ReparsePoint));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        Assert.Equal(3, result.Telemetry.FilesEnumerated);
        Assert.Equal(2, result.Telemetry.FilesPlaceholder);
        Assert.Equal(result.Files.Count, result.Telemetry.FilesEnumerated);
        Assert.Equal(0, result.Telemetry.PlaceholderBytesRead);
    }

    [Fact]
    public void Reparse08_ReparseNuncaTemConteudoLido()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/link.grande", size: 999, attrs: FileAttributes.ReparsePoint),
            Entry("/root/normal.txt"));

        _ = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        // Prova central: enumeração Level 0 NUNCA lê conteúdo — nem de reparse.
        Assert.Equal(0, fake.ReadBytesCallCount);
    }

    // ------------------------------------------------------------------
    // 4 casos de LOOP DETECTION / PROFUNDIDADE
    // ------------------------------------------------------------------

    [Fact]
    public void Loop01_MesmoInodeEmCaminhosDiferentes_DuplicataRejeitadaComErro()
    {
        var fake = new FakePhysicalEnumerator(
            Entry("/root/original.txt", fileId: "inode-777"),
            Entry("/root/loop/copia.txt", fileId: "inode-777")); // volta ao mesmo inode

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        // Fica a PRIMEIRA ocorrência na ordem canônica ("/root/loop/..." < "/root/original..."):
        // decisão determinística, qualquer que seja a ordem física de chegada.
        var caminhos = result.Files.Select(f => f.Path).ToArray();
        Assert.Equal(new[] { "/root/loop/copia.txt" }, caminhos);
        var erro = Assert.Single(result.Errors);
        Assert.Equal("/root/original.txt", erro.Path);
        Assert.Contains("mesmo inode", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loop02_CicloTriplo_ApenasOriginaisFicam_RejeicaoDeterministica()
    {
        // a -> b -> a: três caminhos, dois inodes. A duplicata (qualquer que seja
        // a ordem física de chegada) sempre é a de MAIOR caminho canônico.
        FileEntry[] fisica =
        [
            Entry("/root/b/eco.txt", fileId: "i-2"),
            Entry("/root/a/alvo.txt", fileId: "i-1"),
            Entry("/root/a/loop/b/eco.txt", fileId: "i-2"),
        ];

        var r1 = new OrderedFileEnumerator(new FakePhysicalEnumerator(fisica)).Enumerate("/root", CancellationToken.None);
        var r2 = new OrderedFileEnumerator(new FakePhysicalEnumerator(fisica.Reverse().ToArray())).Enumerate("/root", CancellationToken.None);

        Assert.Equal(r1.Files, r2.Files);
        Assert.Equal(r1.Errors, r2.Errors);
        Assert.Equal(
            new[] { "/root/a/alvo.txt", "/root/a/loop/b/eco.txt" },
            r1.Files.Select(f => f.Path).ToArray());
        Assert.Equal("/root/b/eco.txt", Assert.Single(r1.Errors).Path);
    }

    [Fact]
    public void Loop03_ProfundidadeAcimaDoTeto16_ERejeitada_Exatos16Ficam()
    {
        const string seg = "d";
        var dentro = "/root/" + string.Join('/', Enumerable.Repeat(seg, 15)) + "/limite16.txt";   // depth 16
        var fora = "/root/" + string.Join('/', Enumerable.Repeat(seg, 16)) + "/estouro17.txt";   // depth 17

        var fake = new FakePhysicalEnumerator(Entry(dentro), Entry(fora));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        Assert.Equal(new[] { dentro }, result.Files.Select(f => f.Path).ToArray());
        var erro = Assert.Single(result.Errors);
        Assert.Equal(fora, erro.Path);
        Assert.Contains("profundidade", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loop04_IntegracaoArvoreComCicloReal_TerminaEmTempoFinitoSemDuplicata()
    {
        // Ciclo real a->b->a via symlink de diretório, passado pelo pipeline ordenado
        // completo: termina, sem arquivo duplicado e com as folhas registradas.
        _ = Directory.CreateDirectory(Path.Combine(_raiz, "a"));
        _ = Directory.CreateDirectory(Path.Combine(_raiz, "b"));
        File.WriteAllText(Path.Combine(_raiz, "a", "x.txt"), "x");
        File.WriteAllText(Path.Combine(_raiz, "b", "y.txt"), "y");
        File.CreateSymbolicLink(Path.Combine(_raiz, "a", "loop"), Path.Combine(_raiz, "b"));
        File.CreateSymbolicLink(Path.Combine(_raiz, "b", "loop"), Path.Combine(_raiz, "a"));

        var result = new OrderedFileEnumerator(new CrossPlatformEnumerator())
            .Enumerate(_raiz, CancellationToken.None);

        var nomesTxt = result.Files
            .Where(f => f.Path.EndsWith(".txt", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.Path))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "x.txt", "y.txt" }, nomesTxt);
        Assert.Equal(result.Files.Count, result.Telemetry.FilesEnumerated);
        Assert.Contains(result.Errors, e => e.Path.EndsWith(Path.Combine("a", "loop"), StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Path.EndsWith(Path.Combine("b", "loop"), StringComparison.Ordinal));
    }

    [Fact]
    public void Loop05_Profundidade_CaminhoForaDaRaiz_NuncaExcedeTeto()
    {
        // Simula entrada cuja raiz lógica é distinta da raiz passada: código de Profundidade
        // deve retornar 0 em vez de inflar artificialmente — caso contrário, rejeição por
        // profundidade fere caminhos fora da raiz (segurança: falso-positivo de loop).
        var fake = new FakePhysicalEnumerator(
            Entry("/root/real.txt"),
            Entry("/outro/volume/salto.txt"));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root", CancellationToken.None);

        // Nenhum erro de profundidade — caminho fora da raiz não é rejeitado.
        Assert.Empty(result.Errors.Where(e => e.Message.Contains("profundidade", StringComparison.Ordinal)));
        Assert.Equal(
            new[] { "/outro/volume/salto.txt", "/root/real.txt" },
            result.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Loop06_Profundidade_RaizApenasDiretorio_TamanhoCorreto()
    {
        // Raiz passada como "/root/" (com barra final) e arquivos dentro dela: depth deve
        // ser mesurável mesmo após TrimStart do relativo.
        var fake = new FakePhysicalEnumerator(
            Entry("/root/d1/d2/final.txt"));

        var result = new OrderedFileEnumerator(fake).Enumerate("/root/", CancellationToken.None);

        // Profundidade = 3 ("/d1/d2/final.txt") <= teto (16) — fica.
        Assert.Empty(result.Errors.Where(e => e.Message.Contains("profundidade", StringComparison.Ordinal)));
        Assert.Single(result.Files, f => f.Path.EndsWith("final.txt", StringComparison.Ordinal));
    }
}
