namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T22 (t_807d2357) — SUÍTE PLACEHOLDER ENFORCEMENT (PLH-01..PLH-04).
/// Prova, por espiões de abertura (mocks de IStreamSource que contam aberturas),
/// que o pipeline NUNCA lê placeholder: nem durante o scan, nem por mutação pós-gate.
/// Complementa PlaceholderGateTests/PlaceholderPipelineIntegrationTests com as quatro
/// garantias contratuais do card. NÃO é código de produto.
///
/// PLH-01 — nenhum File.Open em placeholder durante scan;
/// PLH-02 — PlaceholderReadException lançado ANTES de qualquer I/O;
/// PLH-03 — PlaceholderViolationException se telemetria chega com bytes != 0;
/// PLH-04 — mutação pós-gate falha (teste negativo estrutural).
/// </summary>
public class PlaceholderSuiteTests
{
    private static FileEntry Entrada(
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

    private static EnumerationResult Resultado(
        IReadOnlyList<FileEntry> files,
        ScanTelemetry? telemetria = null)
        => new(files, Array.Empty<ScanError>(), telemetria ?? new ScanTelemetry());

    /// <summary>
    /// PLH-01 — Durante o scan completo (L0→L3) de árvore mista (conteúdo normal +
    /// placeholders registrados na fonte), a contagem de aberturas sobre cada
    /// placeholder é ZERO. A fonte espiã tem conteúdo disponível para os placeholders:
    /// se qualquer caminho do produto abrir stream sobre eles, o teste fica vermelho
    /// (SPEC §6 "NÃO TOCAR"; gate placeholder_bytes_read == 0).
    /// </summary>
    [Fact]
    public void Plh01_ScanArvoreMista_NenhumaAberturaDeStreamSobrePlaceholders()
    {
        var fonte = new CountingStreamSource();
        var placeholders = new[]
        {
            ("tree/p.offline", PlaceholderKind.Offline, FileAttributes.Offline),
            ("tree/p.recall", PlaceholderKind.RecallOnOpen, PlaceholderPolicy.RecallOnOpen),
            ("tree/p.data", PlaceholderKind.RecallOnDataAccess, PlaceholderPolicy.RecallOnDataAccess),
            ("tree/p.reparse", PlaceholderKind.ReparsePoint, FileAttributes.ReparsePoint),
        };
        foreach (var (path, _, _) in placeholders)
        {
            fonte.Register(path, new byte[2048]);
        }

        // Par de conteúdo idêntico, nome e tamanho iguais ⇒ colide no L1 e sobrevive ao L2/L3.
        fonte.Register("tree/a/Relatorio.bin", new byte[] { 1, 2, 3 });
        fonte.Register("tree/b/Relatorio.bin", new byte[] { 1, 2, 3 });

        var entries = new List<FileEntry>();
        var placeholderIds = new[] { "p_offline", "p_recall", "p_data", "p_reparse" };
        foreach (var i in Enumerable.Range(0, placeholders.Length))
        {
            var (path, kind, attrs) = placeholders[i];
            // Marcação L0 ausenta OU presente não muda nada: o duplo gate cobre ambos.
            var marcado = path != "tree/p.recall";
            entries.Add(Entrada(path, 2048, attrs, marcado, marcado ? kind : null, placeholderIds[i]));
        }

        entries.Add(Entrada("tree/a/Relatorio.bin", 3, fileId: "fa"));
        entries.Add(Entrada("tree/b/Relatorio.bin", 3, fileId: "fb"));

        var hasher = CountingHasher.Using(new PlaceholderGate(fonte));
        var pipeline = new ScanPipeline(new FakeEnumerator(Resultado(entries)), hasher, new PlaceholderGate(fonte));

        var resultado = pipeline.Run("tree");

        // Scan saiu íntegro: duplicata idêntica detectada, nenhum conflito falso.
        Assert.Single(resultado.IdenticalDuplicates);
        Assert.Empty(resultado.RealConflicts);

        // A prova central: zero aberturas sobre CADA placeholder, nos quatro kinds.
        foreach (var (path, _, _) in placeholders)
        {
            Assert.Equal(0, fonte.OpenCount(path));
            Assert.Equal(0, fonte.BytesRead(path));
        }

        // E o conteúdo legítimo foi lido normalmente (o scan realmente passou pelo L3).
        Assert.True(fonte.OpenCount("tree/a/Relatorio.bin") >= 1);
        Assert.True(fonte.OpenCount("tree/b/Relatorio.bin") >= 1);
    }

    /// <summary>
    /// PLH-02 — PlaceholderReadException sai ANTES de qualquer I/O: o hasher espião
    /// atravessa o gate e a fonte espiã conta aberturas; ao tentar hashear placeholder
    /// (flag L0 e, em separado, só com bits crus — duplo gate), nenhuma abertura é
    /// registrada ANTES da exceção (ordem gate→I/O provada pela contagem == 0).
    /// </summary>
    [Fact]
    public void Plh02_HashSobrePlaceholder_LancaAntesDeQualquerIo()
    {
        var fonte = new CountingStreamSource();
        fonte.Register("tree/p.offline", new byte[1024]);

        // Caso 1: flag L0 marcada.
        var marcado = Entrada("tree/p.offline", 1024, FileAttributes.Offline, true, PlaceholderKind.Offline);
        var hasherMarcado = new PlaceholderGuardedHasher(CountingHasher.Using(fonte));

        var ex = Assert.Throws<PlaceholderReadException>(() => hasherMarcado.FullHash(marcado));

        Assert.Equal("tree/p.offline", ex.EntryPath);
        Assert.Equal(0, fonte.OpenCount("tree/p.offline"));
        Assert.Equal(0, fonte.BytesRead("tree/p.offline"));

        // Caso 2: marcação ausente, bits crus revelam placeholder (marcação stale nunca passa).
        var disfarçada = Entrada("tree/p.offline", 1024, FileAttributes.Offline);
        var hasherDisfarçada = new PlaceholderGuardedHasher(CountingHasher.Using(fonte));

        Assert.Throws<PlaceholderReadException>(() => hasherDisfarçada.PartialHash(disfarçada));

        Assert.Equal(0, fonte.OpenCount("tree/p.offline"));
        Assert.Equal(0, fonte.BytesRead("tree/p.offline"));

        // Controle positivo: arquivo normal DELEGA à fonte e abre exatamente 1 vez.
        fonte.Register("tree/ok.bin", new byte[] { 9 });
        hasherMarcado.FullHash(Entrada("tree/ok.bin", 1));
        Assert.Equal(1, fonte.OpenCount("tree/ok.bin"));
    }

    /// <summary>
    /// PLH-03 — Telemetria que chega ao gate com placeholder_bytes_read != 0 é violação
    /// de segurança, não dado: PlaceholderViolationException SEM caminho, o gate nunca
    /// "lava" o contador e a saída (quando existe) mantém o valor zerado por construção.
    /// </summary>
    [Fact]
    public void Plh03_TelemetriaComBytesDePlaceholder_LancaViolacaoENuncaELavada()
    {
        var fonte = new CountingStreamSource();

        foreach (var suja in new[]
                 {
                     new ScanTelemetry { PlaceholderBytesRead = 1 },
                     new ScanTelemetry { PlaceholderBytesRead = 4096 },
                     new ScanTelemetry { PlaceholderBytesRead = -3 }, // nem negativo passa
                 })
        {
            var ex = Assert.Throws<PlaceholderViolationException>(
                () => new PlaceholderGate(fonte).Enforce(Resultado(Array.Empty<FileEntry>(), suja)));

            Assert.Null(ex.EntryPath); // violação de telemetria não carrega caminho
        }

        // Controle: telemetria limpa atravessa e sai com placeholder_bytes_read == 0.
        var limpa = new PlaceholderGate(fonte).Enforce(Resultado(Array.Empty<FileEntry>(), new ScanTelemetry()));
        Assert.Equal(0, limpa.Telemetry.PlaceholderBytesRead);
    }

    /// <summary>
    /// PLH-04 — Mutação pós-gate falha: remover/burlar o gate deixa a violação visível.
    /// Estrutura em três provas:
    /// (a) sem gate, a abertura de placeholder "consegue" ler — exatamente o estado que
    ///     os espiões denunciam (contagem > 0 ⇒ qualquer suíte com estes testes fica vermelha);
    /// (b) o gate real recusa a mesma abertura com PlaceholderViolationException ANTES de
    ///     tocar a fonte (contagem permanece 0);
    /// (c) invariante final do relatório: placeholder_bytes_read == 0 após Enforce.
    /// </summary>
    [Fact]
    public void Plh04_MutacaoPosGate_Falha_EhDetectadaPelosEspioes()
    {
        const string path = "tree/p.offline";

        // (a) Mutante simulado: código pós-gate sem proteção abre e lê o placeholder.
        var fonteMutante = new CountingStreamSource();
        fonteMutante.Register(path, new byte[512]);
        var placeholder = Entrada(path, 512, FileAttributes.Offline, true, PlaceholderKind.Offline);

        using (var s = fonteMutante.OpenRead(placeholder))
        {
            var buffer = new byte[256];
            while (s.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }

        Assert.True(fonteMutante.OpenCount(path) > 0, "mutação deve conseguir abrir (senão o teste não prova nada)");
        Assert.True(fonteMutante.BytesRead(path) > 0);

        // (b) Produto real: o mesmo acesso, agora sob o gate, falha ANTES da fonte.
        var fonteProduto = new CountingStreamSource();
        fonteProduto.Register(path, new byte[512]);
        var gate = new PlaceholderGate(fonteProduto);

        Assert.Throws<PlaceholderViolationException>(() => gate.OpenRead(placeholder));
        Assert.Equal(0, fonteProduto.OpenCount(path));
        Assert.Equal(0, fonteProduto.BytesRead(path));

        // (c) Invariante estrutural pós-gate: telemetria derivada trava o contador em zero.
        var enumeration = Resultado(new[]
        {
            placeholder,
            Entrada("tree/a.bin", 1),
        });
        var parcial = new PlaceholderGate(new CountingStreamSource()).Enforce(enumeration);

        Assert.Single(parcial.Placeholders);
        Assert.Single(parcial.Files);
        Assert.Equal(0, parcial.Telemetry.PlaceholderBytesRead);
    }

    /// <summary>Dupla L0 fake para o pipeline (só devolve o resultado pronto).</summary>
    private sealed class FakeEnumerator : IFileEnumerator
    {
        private readonly EnumerationResult _resultado;

        public FakeEnumerator(EnumerationResult resultado) => _resultado = resultado;

        public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default) => _resultado;
    }
}
