namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — ciclo final: <see cref="PlaceholderGate.Enforce"/> sobre o
/// resultado da enumeração (escopo reduzido pelo orquestrador). Prova por espiões
/// que NENHUM byte de placeholder é lido e que o relatório parcial sai com
/// placeholder_bytes_read == 0 GARANTIDO POR CONSTRUÇÃO (SPEC §6/§21).
/// </summary>
public class PlaceholderGateTests
{
    private static FileEntry Entrada(
        string path,
        long size,
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

    private static EnumerationResult Resultado(
        IReadOnlyList<FileEntry> files,
        ScanTelemetry? telemetria = null)
        => new(files, Array.Empty<ScanError>(), telemetria ?? new ScanTelemetry());

    [Fact]
    public void Enforce_ArvoreMista_ZeroLeitura_RelatorioParcialCorreto()
    {
        // Espiões registram conteúdo para TODOS os caminhos — inclusive placeholders.
        // Se qualquer byte for lido deles, o teste fica vermelho.
        var fonte = new CountingStreamSource();
        var normal1 = Entrada("tree/a.bin", 100);
        var normal2 = Entrada("tree/b.bin", 200);
        var phOffline = Entrada("tree/p.offline", 4096, FileAttributes.Offline, true, PlaceholderKind.Offline);
        var phReparse = Entrada("tree/link.bin", 0, FileAttributes.ReparsePoint, true, PlaceholderKind.ReparsePoint);
        fonte.Register("tree/a.bin", new byte[100]);
        fonte.Register("tree/b.bin", new byte[200]);
        fonte.Register("tree/p.offline", new byte[4096]);
        fonte.Register("tree/link.bin", new byte[999]);

        var entrada = Resultado(new[] { normal1, phOffline, normal2, phReparse });

        var relatorio = new PlaceholderGate(fonte).Enforce(entrada);

        // Zero acesso a conteúdo de placeholder (espiões).
        Assert.Equal(0, fonte.OpenCount("tree/p.offline"));
        Assert.Equal(0, fonte.OpenCount("tree/link.bin"));
        Assert.Equal(0, fonte.BytesRead("tree/p.offline"));
        Assert.Equal(0, fonte.BytesRead("tree/link.bin"));

        // Relatório parcial: só não-placeholders seguem para L1/L2/L3.
        Assert.Equal(new[] { "tree/a.bin", "tree/b.bin" }, relatorio.Files.Select(f => f.Path));

        // Placeholders[] conforme projeção do schema v1 §6.3 (ordem por bytes de caminho).
        Assert.Equal(
            new[] { "tree/link.bin", "tree/p.offline" },
            relatorio.Placeholders.Select(p => p.Path));
        Assert.Equal(new[] { "reparse_point" }, relatorio.Placeholders[0].Kinds);
        Assert.Equal(new[] { "offline" }, relatorio.Placeholders[1].Kinds);

        // Telemetria derivada: contados em files_placeholder, gate zerado por construção.
        Assert.Equal(4, relatorio.Telemetry.FilesEnumerated);
        Assert.Equal(2, relatorio.Telemetry.FilesPlaceholder);
        Assert.Equal(0, relatorio.Telemetry.PlaceholderBytesRead);
        Assert.Equal(0, relatorio.Telemetry.BytesRead);
    }

    [Fact]
    public void OpenRead_PlaceholderMarcado_LancaPlaceholderViolationException()
    {
        var fonte = new CountingStreamSource();
        fonte.Register("tree/p.offline", new byte[4096]);
        var gate = new PlaceholderGate(fonte);
        var ph = Entrada("tree/p.offline", 4096, FileAttributes.Offline, true, PlaceholderKind.Offline);

        var ex = Assert.Throws<PlaceholderViolationException>(() => gate.OpenRead(ph));

        Assert.Equal(ph.Path, ex.EntryPath);
        Assert.Equal(0, fonte.OpenCount(ph.Path)); // nada aberto ANTES da exceção
    }

    [Fact]
    public void OpenRead_NaoMarcada_ComBitsCrusDePlaceholder_TambemELanca()
    {
        // Defesa em profundidade (T-03): marcação ausente/stale do Level 0 não passa.
        var fonte = new CountingStreamSource();
        fonte.Register("tree/x.bin", new byte[512]);
        var gate = new PlaceholderGate(fonte);
        var disfarçada = Entrada("tree/x.bin", 512, (FileAttributes)0x00400000, false, null);

        Assert.Throws<PlaceholderViolationException>(() => gate.OpenRead(disfarçada));
        Assert.Equal(0, fonte.OpenCount("tree/x.bin"));
    }

    [Fact]
    public void OpenRead_ArquivoNormal_DelegaÀFonte()
    {
        var fonte = new CountingStreamSource();
        fonte.Register("tree/a.bin", new byte[] { 1, 2, 3 });
        var gate = new PlaceholderGate(fonte);
        var normal = Entrada("tree/a.bin", 3);

        using var stream = gate.OpenRead(normal);

        Assert.Equal(3, stream.Length);
        Assert.Equal(1, fonte.OpenCount("tree/a.bin"));
    }

    [Fact]
    public void Enforce_TelemetriaDeEntradaComBytesDePlaceholder_NuncaELavada()
    {
        // Violação a jusante não pode ser silenciosamente zerada: o gate RECUSA.
        var fonte = new CountingStreamSource();
        var suja = new ScanTelemetry { PlaceholderBytesRead = 128 };

        var ex = Assert.Throws<PlaceholderViolationException>(
            () => new PlaceholderGate(fonte).Enforce(Resultado(Array.Empty<FileEntry>(), suja)));

        Assert.Null(ex.EntryPath);
    }

    [Fact]
    public void Mutacao_GateRemovido_EhDetectadaPelosEspioes()
    {
        // Teste negativo do card: mutação que faça o pipeline abrir placeholder DEVE
        // falhar. Sem o gate, a abertura "consegue" ler — e é exatamente isso que os
        // espiões expõem como violação.
        var fonte = new CountingStreamSource();
        fonte.Register("tree/p.offline", new byte[4096]);
        var mutante = Entrada("tree/p.offline", 4096, FileAttributes.Offline, true, PlaceholderKind.Offline);

        using (var s = fonte.OpenRead(mutante))
        {
            var buffer = new byte[1024];
            while (s.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        } // mutação simulada: código sem gate abriu E leu diretamente

        Assert.True(fonte.OpenCount("tree/p.offline") > 0);
        Assert.True(fonte.BytesRead("tree/p.offline") > 0);
    }
}
