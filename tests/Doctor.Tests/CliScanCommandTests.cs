namespace Doctor.Tests;

using System.Text.Json;
using Doctor.Cli;
using Doctor.Core;

/// <summary>
/// T16 (t_71afe316) — CLI 'conflictdoctor scan &lt;path&gt; [--json] [--quiet]' (SPEC §14,
/// EPIC 09). Invocação IN-PROCESS via ScanCommand.Run com árvore em tmp cobrindo os
/// 4 exit codes documentados + validade estrutural do JSON v1. A CLI é camada fina:
/// compõe o pipeline de produção exatamente como o card T12 a definiu
/// (Sidecar → Ordered → CrossPlatform; Blake3Hasher), formata e traduz veredito.
/// </summary>
public sealed class CliScanCommandTests : IDisposable
{
    private const int Kib = 1024;

    // > 128 KiB: hash parcial = janelas [0,64K)+[fim-64K,fim); miolos distintos forçam
    // colisão parcial com full hash divergente ⇒ conflito real EXIGE o Level 3.
    private const int ArquivoConflito = 200 * Kib;
    private const int Janela = 64 * Kib;

    private readonly string _root;

    public CliScanCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"t16-cli-{Guid.NewGuid():N}");
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
            // limpeza best-effort: tmp do SO recolhe depois (padrão da suíte)
        }
    }

    // ---- EXIT 0 — árvore limpa -------------------------------------------------------

    [Fact]
    public void ArvoreLimpa_SemJson_SemQuiet_ExitZero_TextoResumo()
    {
        var antes = new HashSet<string>(Directory.EnumerateFileSystemEntries(_root));
        File.WriteAllText(Caminho("leia-me.txt"), "arquivo unico, sem par");
        var depois = new HashSet<string>(Directory.EnumerateFileSystemEntries(_root));

        var r = ScanCommand.Run(new[] { "scan", _root });

        Assert.Equal(0, r.ExitCode);
        Assert.Null(r.JsonOutput);
        Assert.NotNull(r.HumanText);
        Assert.DoesNotContain("duplicat", r.HumanText, StringComparison.OrdinalIgnoreCase);
        // NUNCA delete: a CLI não toca no conteúdo do usuário — nem lê além da lista,
        // nem escreve nada de volta (sem cache/sidecar criado dentro da raiz escaneada).
        Assert.Equal(depois, new HashSet<string>(Directory.EnumerateFileSystemEntries(_root)));
    }

    [Fact]
    public void ArvoreLimpa_ComJson_ExitZero_JsonValidoSemAnomalias()
    {
        File.WriteAllText(Caminho("unico.dat"), "conteudo qualquer");

        var r = ScanCommand.Run(new[] { "scan", _root, "--json" });

        Assert.Equal(0, r.ExitCode);
        Assert.Null(r.HumanText);
        var doc = JsonDocument.Parse(r.JsonOutput!); // JSON v1 válido
        Assert.Equal(1, doc.RootElement.GetProperty("report_schema_version").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("identical_duplicates").EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("real_conflicts").EnumerateArray());
    }

    // ---- EXIT 2 — duplicatas idênticas ----------------------------------------------

    [Fact]
    public void DuplicatasIdenticas_SemFlags_ExitDois_ListaNoTexto()
    {
        var conteudo = "par de duplicatas identicas para o exit code 2";
        // Mesmo NOME BASE normalizado em diretórios distintos: o agrupamento da SPEC §7
        // é por (normalized_base_name, size) — nomes distintos nunca são duplicatas.
        Directory.CreateDirectory(Caminho("docs"));
        File.WriteAllText(Caminho("docs/foto.txt"), conteudo);
        Directory.CreateDirectory(Path.Combine(_root, "docs", "backup"));
        File.WriteAllText(Caminho("docs/backup/foto.txt"), conteudo);

        var r = ScanCommand.Run(new[] { "scan", _root });

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("foto.txt", r.HumanText);
        Assert.DoesNotContain("PARCIAL", r.HumanText);
    }

    [Fact]
    public void DuplicatasIdenticas_ComJson_ExitDois_JsonComDuplicata()
    {
        var conteudo = new byte[64];
        Random.Shared.NextBytes(conteudo);
        Directory.CreateDirectory(Caminho("a"));
        Directory.CreateDirectory(Caminho("b"));
        File.WriteAllBytes(Caminho("a/dados.bin"), conteudo);
        File.WriteAllBytes(Caminho("b/dados.bin"), conteudo);

        var r = ScanCommand.Run(new[] { "scan", _root, "--json" });

        Assert.Equal(2, r.ExitCode);
        var doc = JsonDocument.Parse(r.JsonOutput!);
        var dup = doc.RootElement.GetProperty("identical_duplicates");
        Assert.Single(dup.EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("real_conflicts").EnumerateArray());
        // BLAKE3 hex minúscula de 32 bytes (ADR-0005 §1) — 64 caracteres.
        var hash = dup[0].GetProperty("hash").GetString();
        Assert.Equal(64, hash!.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    // ---- EXIT 2 — conflito real exige L3 (mesmo size, janelas iguais, miolo distinto)

    [Fact]
    public void ConflitoReal_ExigeLevel3_ExitDois_JsonComConflito()
    {
        File.WriteAllBytes(Caminho("orcamento.xlsx"), ConteudoConflito(0x11));
        File.WriteAllBytes(Caminho("orcamento-DESKTOP-ABC123.xlsx"), ConteudoConflito(0x22));

        var r = ScanCommand.Run(new[] { "scan", _root, "--json" });

        Assert.Equal(2, r.ExitCode);
        var doc = JsonDocument.Parse(r.JsonOutput!);
        var conflitos = doc.RootElement.GetProperty("real_conflicts");
        Assert.Single(conflitos.EnumerateArray());
        Assert.Equal("orcamento.xlsx", conflitos[0].GetProperty("normalized_base_name").GetString());
        Assert.Equal(2, conflitos[0].GetProperty("files").GetArrayLength());
        Assert.NotEqual(
            conflitos[0].GetProperty("files")[0].GetProperty("hash").GetString(),
            conflitos[0].GetProperty("files")[1].GetProperty("hash").GetString());
    }

    // ---- EXIT 3 — parcial: anomalias E arquivos pulados -----------------------------

    [Fact]
    public void AnomaliasComArquivosPulados_ExitTres_MarcaParcial()
    {
        var conteudo = "conteudo compartilhado entre as duas copias do par";
        File.WriteAllText(Caminho("doc.txt"), conteudo);
        Directory.CreateDirectory(Caminho("copia"));
        File.WriteAllText(Caminho("copia/doc.txt"), conteudo);

        // Erro de acesso individual injetado na fronteira da CLI (contratos.md R10:
        // erro não aborta o scan; o veredito parcial é decisão da CAMADA CLI).
        ComErroDeAcessoSimulado(() =>
        {
            var r = ScanCommand.Run(new[] { "scan", _root });
            Assert.Equal(3, r.ExitCode);
            Assert.Contains("doc.txt", r.HumanText);      // anomalia listada
            Assert.Contains("intocavel.bin", r.HumanText); // pulado listado
            Assert.Contains("PARCIAL", r.HumanText);
        });
    }

    [Fact]
    public void ErrosSemAnomalias_PermaneceExitZero()
    {
        // Parcial (3) exige anomalia E pulado; erro sem anomalias não sobe para 3.
        File.WriteAllText(Caminho("unico-sem-par.txt"), "arquivo solitario");

        ComErroDeAcessoSimulado(() =>
        {
            var r = ScanCommand.Run(new[] { "scan", _root });
            Assert.Equal(0, r.ExitCode);
            Assert.Contains("intocavel.bin", r.HumanText); // evidência do pulado
        });
    }

    /// <summary>Injeta um ScanError sintético sobre o L0 real e restaura ao final.</summary>
    private void ComErroDeAcessoSimulado(Action prova)
    {
        var original = ScanCommand.DefaultEnumeration;
        ScanCommand.DefaultEnumeration = raiz =>
        {
            var resultado = original(raiz);
            var comErro = resultado.Errors.Append(
                new ScanError(Path.Combine(_root, "vedado", "intocavel.bin"), "permissao negada (simulada)")).ToArray();
            return resultado with { Errors = comErro };
        };

        try
        {
            prova();
        }
        finally
        {
            ScanCommand.DefaultEnumeration = original;
        }
    }

    // ---- EXIT 1 — erros operacionais -------------------------------------------------

    [Fact]
    public void UsoInvalido_ExitUm_MensagemNoStderr()
    {
        Assert.Equal(1, ScanCommand.Run(new[] { "scan" }).ExitCode);
        Assert.Equal(1, ScanCommand.Run(Array.Empty<string>()).ExitCode);
        Assert.Equal(1, ScanCommand.Run(new[] { "comando-desconhecido", "/tmp" }).ExitCode);
        Assert.Equal(1, ScanCommand.Run(new[] { "scan", _root, "--flag-inexistente" }).ExitCode);
    }

    [Fact]
    public void RaizInexistente_ExitUm_MensagemOperacional()
    {
        var fantasma = Path.Combine(_root, "nao-existe");

        var r = ScanCommand.Run(new[] { "scan", fantasma });

        Assert.Equal(1, r.ExitCode);
        Assert.Null(r.JsonOutput);
        Assert.Contains(fantasma, r.HumanText);
    }

    [Fact]
    public void RaizArquivo_NaoDiretorio_ExitUm()
    {
        var arquivo = Caminho("um-arquivo.txt");
        File.WriteAllText(arquivo, "não é raiz de scan");

        Assert.Equal(1, ScanCommand.Run(new[] { "scan", arquivo }).ExitCode);
    }

    // ---- --quiet ----------------------------------------------------------------------

    [Fact]
    public void Quiet_Duplicatas_ExitDois_TextoVazio()
    {
        var conteudo = "mesmo conteudo nas duas copias para quiet";
        Directory.CreateDirectory(Caminho("q1"));
        Directory.CreateDirectory(Caminho("q2"));
        File.WriteAllText(Caminho("q1/x.txt"), conteudo);
        File.WriteAllText(Caminho("q2/x.txt"), conteudo);

        var r = ScanCommand.Run(new[] { "scan", _root, "--quiet" });

        Assert.Equal(2, r.ExitCode);
        Assert.Null(r.JsonOutput);
        Assert.Equal(string.Empty, r.HumanText); // silencioso mesmo com anomalia
    }

    // ---- Determinismo do relatório pela CLI (ADR-0003 regra CI mínima) ---------------

    [Fact]
    public void Json_DuasExecucoesNaMesmaArvore_IguaisByteAByte()
    {
        var conteudo = "determinismo do json emitido pela cli";
        Directory.CreateDirectory(Caminho("d1"));
        Directory.CreateDirectory(Caminho("d2"));
        File.WriteAllText(Caminho("d1/p.txt"), conteudo);
        File.WriteAllText(Caminho("d2/p.txt"), conteudo);

        var r1 = ScanCommand.Run(new[] { "scan", _root, "--json" });
        var r2 = ScanCommand.Run(new[] { "scan", _root, "--json" });

        // ADR-0003: timestamps vivem SÓ em generated_from — mascarados, todo o resto
        // do relatório v1 tem de ser idêntico byte a byte entre execuções.
        Assert.Equal(
            MascararTimestamps(r1.JsonOutput!),
            MascararTimestamps(r2.JsonOutput!));
    }

    /// <summary>Campos de relógio de parede do schema v1 (ADR-0003): variam por
    /// execução; tudo o demais é canônico. Mesmo padrão do T13 (ReportWriterTests).</summary>
    private static string MascararTimestamps(string json) =>
        System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"(scan_started_utc|scan_finished_utc)\": \"[^\"]*\"",
            "$1: \"MASKED\"");

    [Fact]
    public void PlaceholderNuncaELido_ContaComoNaoAnomalia()
    {
        // Placeholders entram na lista §6.3 mas NÃO são anomalia de conflito/duplicata.
        var alvo = Caminho("offline.bin");
        File.WriteAllBytes(alvo, new byte[4096]);
        File.WriteAllText(alvo + ".placeholder-meta.json", "{\"kind\":\"offline\"}");

        var r = ScanCommand.Run(new[] { "scan", _root, "--json" });

        Assert.Equal(0, r.ExitCode); // placeholder isolado não vira exit 2/3
        var doc = JsonDocument.Parse(r.JsonOutput!);
        var ph = doc.RootElement.GetProperty("placeholders");
        Assert.Single(ph.EnumerateArray());
        Assert.Equal(0, doc.RootElement.GetProperty("telemetry").GetProperty("placeholder_bytes_read").GetInt64());
    }

    // ---- fixtures ---------------------------------------------------------------------

    private string Caminho(string relativo)
    {
        var partes = relativo.Split('/');
        return Path.Combine(new[] { _root }.Concat(partes).ToArray());
    }

    /// <summary>Cabeça e cauda fixas (colisão parcial garantida no L2); miolo varia —
    /// mesmo padrão do teste DET-03 do T12.</summary>
    private static byte[] ConteudoConflito(byte miolo)
    {
        var bytes = new byte[ArquivoConflito];

        for (var i = 0; i < Janela; i++)
        {
            bytes[i] = (byte)(0xAA + (i % 13));
        }

        for (var i = ArquivoConflito - Janela; i < ArquivoConflito; i++)
        {
            bytes[i] = (byte)(0xBB + (i % 17));
        }

        for (var i = Janela; i < ArquivoConflito - Janela; i++)
        {
            bytes[i] = miolo;
        }

        return bytes;
    }
}
