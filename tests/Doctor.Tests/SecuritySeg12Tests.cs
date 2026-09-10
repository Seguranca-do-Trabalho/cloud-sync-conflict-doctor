namespace Doctor.Tests;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Doctor.Cli;
using Doctor.Core;

/// <summary>
/// SEG-12 (t_694bc7ce; adendo T-15 do security-audit-gate5; caso T-06 do threat-model,
/// regra R1): a quarentena publica em &lt;raiz&gt;/ConflictDoctor/quarantine/&lt;op_id&gt;/
/// DENTRO da raiz escaneada (ADR-0002/SPEC §18), então um segundo scan sobre a mesma raiz
/// encontraría os payloads .dat como candidatos — poluindo o relatório e quebrando a
/// idempotência §20. A enumeração Level 0 exclui a subárvore reservada por comparação de
/// PREFIXO EM BYTES do caminho canônico e conta as entradas excluídas em
/// files_excluded_conflictdoctor (schema v2, §5/§7.1 do schema-report-v1).
///
/// Fluxo exercitado é o de produção inteiro: ScanCommand.Run compõe
/// SidecarPlaceholderEnumerator(OrderedFileEnumerator(CrossPlatformEnumerator)) +
/// ScanPipeline L0→L3 + ReportWriterJson — nenhuma peça de teste no caminho.
/// NUNCA File.Delete: a quarentena nasce e permanece (ADR-0002).
/// </summary>
[Collection("ScanCommand")]
public sealed class SecuritySeg12Tests : IDisposable
{
    private readonly string _root;

    public SecuritySeg12Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "seg12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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
    public void Security_QuarantineInsideScannedRoot_ExcludedFromEnumeration()
    {
        // ---- árvore inicial: par idêntico + arquivo único -----------------------------
        var conteudoPar = "conteudo identico do par - seg12 - versao unica";
        Directory.CreateDirectory(Caminho("docs", "backup"));
        File.WriteAllText(Caminho("docs", "foto.txt"), conteudoPar);
        File.WriteAllText(Caminho("docs", "backup", "foto.txt"), conteudoPar);
        File.WriteAllText(Caminho("leiame.txt"), "arquivo unico\n");

        // Scan 1 (pré-resolução): o par aparece como duplicata (exit 2) — saneidade.
        var scan1 = ScanCommand.Run(["scan", _root, "--json"]);
        Assert.Equal(2, scan1.ExitCode);
        Assert.Contains("docs/backup/foto.txt", scan1.JsonOutput, StringComparison.Ordinal);

        // ---- resolução REAL: move a duplicata para a quarentena §18 (dentro da raiz) --
        var enumerador = new SidecarPlaceholderEnumerator(
            new OrderedFileEnumerator(new CrossPlatformEnumerator()));
        var snapshot = enumerador.Enumerate(_root);
        var duplicata = snapshot.Files.Single(e => e.Path == Caminho("docs", "backup", "foto.txt"));

        var operacao = new QuarantineService().Move(
            [new QuarantineItem(duplicata, "IDENTICAL_DUPLICATE", "KEEP_NEWEST")],
            new QuarantinePlan(_root, new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero)));

        // Pré-condição do caso T-06: a quarentena foi publicada DENTRO da raiz escaneada.
        Assert.True(Directory.Exists(Path.Combine(
            _root, "ConflictDoctor", "quarantine", operacao.OperationId)));
        // Payload .dat + manifesto existem fisicamente dentro da árvore escaneada.
        Assert.True(File.Exists(Path.Combine(
            _root, "ConflictDoctor", "quarantine", operacao.OperationId, "payload", "0001.dat")));
        Assert.True(File.Exists(Path.Combine(
            _root, "ConflictDoctor", "quarantine", operacao.OperationId, "manifest.json")));

        // ---- dois rescans da MESMA árvore pós-resolução --------------------------------
        var scan2 = ScanCommand.Run(["scan", _root, "--json"]);
        var scan3 = ScanCommand.Run(["scan", _root, "--json"]);

        Assert.Equal(0, scan2.ExitCode); // duplicata resolvida: árvore limpa para o produto
        Assert.Equal(0, scan3.ExitCode);

        // 1. ZERO itens de ConflictDoctor/ no relatório — nem payload, nem manifesto,
        //    nem qualquer entrada sob a subárvore reservada (caminhos são relativos à raiz).
        Assert.DoesNotContain("ConflictDoctor", scan2.JsonOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ConflictDoctor", scan3.JsonOutput, StringComparison.Ordinal);

        // 2. Contador dedicado > 0: payload .dat + manifest.json foram excluídos e CONTADOS.
        using var doc2 = JsonDocument.Parse(scan2.JsonOutput!);
        var excluidos = doc2.RootElement
            .GetProperty("telemetry")
            .GetProperty("files_excluded_conflictdoctor")
            .GetInt64();
        Assert.True(excluidos >= 2, $"esperado >= 2 entradas excluidas, obtido {excluidos}");

        // 3. Idempotência §20: segundo scan byte-idêntico ao terceiro (máscara só nos
        //    timestamps de parede, condição §1.6 do schema-report-v1).
        Assert.Equal(Mascarar(scan2.JsonOutput!), Mascarar(scan3.JsonOutput!));

        // Exclusão é política, não erro: nada vira ScanError (contratos.md R10).
        var pos = enumerador.Enumerate(_root);
        Assert.Empty(pos.Errors);
        Assert.Equal(excluidos, pos.Telemetry.FilesExcludedConflictDoctor);
    }

    [Fact]
    public void Security_ArvoreSemConflictDoctor_ContadorZero_ESaidaInalterada()
    {
        // Árvore SEM subárvore reservada: par idêntico com MESMO nome-base (agrupamento
        // SPEC §7 é por normalized_base_name + size — nomes distintos nunca são
        // duplicatas; correção da run anterior deste card) + único, composição comum.
        var conteudo = "par simples sem quarentena";
        Directory.CreateDirectory(Caminho("dados", "backup"));
        File.WriteAllText(Caminho("dados", "a.txt"), conteudo);
        File.WriteAllText(Caminho("dados", "backup", "a.txt"), conteudo);
        File.WriteAllText(Caminho("raiz.txt"), "unico\n");

        var scanA = ScanCommand.Run(["scan", _root, "--json"]);
        var scanB = ScanCommand.Run(["scan", _root, "--json"]);

        Assert.Equal(2, scanA.ExitCode); // o par continua sendo reportado — saída inalterada

        using var docA = JsonDocument.Parse(scanA.JsonOutput!);
        using var docB = JsonDocument.Parse(scanB.JsonOutput!);

        // Contador zerado quando não há nada a excluir.
        Assert.Equal(0, docA.RootElement.GetProperty("telemetry").GetProperty("files_excluded_conflictdoctor").GetInt64());
        Assert.Equal(0, docB.RootElement.GetProperty("telemetry").GetProperty("files_excluded_conflictdoctor").GetInt64());

        // Enumerado = exatamente os 3 arquivos criados (nada a mais, nada a menos).
        Assert.Equal(3, docA.RootElement.GetProperty("telemetry").GetProperty("files_enumerated").GetInt64());

        // Determinismo preservado: scans repetidos continuam byte-idênticos (§20).
        Assert.Equal(Mascarar(scanA.JsonOutput!), Mascarar(scanB.JsonOutput!));

        // A duplicata segue visível no relatório — a exclusão não alcança conteúdo do usuário.
        Assert.Contains("dados/backup/a.txt", scanA.JsonOutput!, StringComparison.Ordinal);
    }

    private string Caminho(params string[] segmentos)
    {
        var todos = new List<string> { _root };
        todos.AddRange(segmentos);
        return Path.Combine(todos.ToArray());
    }

    /// <summary>Máscara §1.6 do schema-report-v1: só os timestamps de parede variam entre
    /// scans reais; todo o restante deve coincidir byte a byte.</summary>
    private static string Mascarar(string json) => Regex.Replace(
        json,
        "\"scan_(started|finished)_utc\": \"[^\"]+\"",
        "\"scan_$1_utc\": \"MASKED-FOR-DETERMINISM-TEST\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));
}
