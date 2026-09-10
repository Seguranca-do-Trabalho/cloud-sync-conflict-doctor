namespace Doctor.Cli;

using System.Globalization;
using Doctor.Core;

/// <summary>
/// T16 — resultado testável da execução do comando scan (skill
/// tdd-deterministic-tooling: CLI como camada fina, run(argv) -> (exit_code, texto);
/// terminal nunca é necessário para testar).
/// </summary>
/// <param name="ExitCode">
/// 0 sem anomalias, 1 erro operacional, 2 duplicatas/conflitos, 3 parcial (SPEC §14).
/// </param>
/// <param name="JsonOutput">JSON v1 completo quando --json; nulo nos demais modos.</param>
/// <param name="HumanText">Texto humano (--quiet: vazio); nulo no modo JSON.</param>
public sealed record CliResult(int ExitCode, string? JsonOutput, string? HumanText);

/// <summary>
/// Comando 'conflictdoctor scan &lt;path&gt; [--json] [--quiet]' sobre o pipeline
/// existente (ScanPipeline L0→L3 + ReportWriterJson v1). Nenhuma decisão nova de
/// domínio: a CLI só compõe, formata e traduz o veredito em exit code estável.
///
/// Contratos respeitados:
/// - ADR-0002: nenhuma API de deleção — a CLI apenas lê e escreve o relatório;
/// - ADR-0003: relatório determinístico; timestamps vivem só em generated_from;
/// - contratos.md R10: erro individual de acesso não aborta o scan; com anomalia
///   E arquivos pulados o veredito é parcial (exit 3), senão 0/2;
/// - falha fechada: qualquer exceção do domínio vira exit 1 com mensagem única,
///   nunca stack trace cru.
/// </summary>
public static class ScanCommand
{
    /// <summary>
    /// Fonte L0 de produção (T12/T13): convenção de sidecar (T04) sobre a ordenação
    /// canônica sobre a enumeração física multiplataforma. Injetável para testes.
    /// </summary>
    public static Func<string, EnumerationResult> DefaultEnumeration { get; set; } =
        root => new SidecarPlaceholderEnumerator(
            new OrderedFileEnumerator(EnumeradorDaPlataforma())).Enumerate(root);

    /// <summary>
    /// Escolhe o enumerador Level 0 conforme o sistema operacional.
    ///
    /// A producao instanciava SEMPRE o CrossPlatformEnumerator, apesar do
    /// WindowsNativeEnumerator existir justamente para o Windows (ADR-0004,
    /// card T23). Isso importava muito mais do que desempenho:
    ///
    /// O CrossPlatformEnumerator obtem o file id por lstat(2) — um P/Invoke de
    /// libc que so existe no POSIX. No Windows ele nao tem de onde tirar um id
    /// real e devolve "0" para TODOS os arquivos. Como OrderedFileEnumerator
    /// usa a chave (VolumeId, FileId) para detectar ciclo de reparse point, o
    /// primeiro arquivo entrava e todos os seguintes eram rejeitados como
    /// "caminho volta ao mesmo inode ja visitado". Na pratica, um scan no
    /// Windows enxergava um unico arquivo por volume.
    ///
    /// O enumerador nativo obtem o FileId NTFS de 128 bits real
    /// (GetFileInformationByHandleEx / FILE_ID_INFO) e o numero de serie do
    /// volume, que e o par correto para essa deteccao.
    /// </summary>
    internal static IFileEnumerator EnumeradorDaPlataforma() =>
        OperatingSystem.IsWindows()
            ? new WindowsNativeEnumerator()
            : new CrossPlatformEnumerator();

    public static CliResult Run(string[] args)
    {
        // ---- gramática fechada antes de qualquer uso (skill tdd-deterministic-tooling)
        // 'scan' é subcomando obrigatório e consumido explicitamente (T16).
        if (args.Length == 0 || !string.Equals(args[0], "scan", StringComparison.Ordinal))
        {
            return Operacional("uso invalido: comando desconhecido. sintaxe: conflictdoctor scan <caminho> --json --quiet");
        }

        var json = false;
        var quiet = false;
        string? raiz = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "--json", StringComparison.Ordinal))
            {
                json = true;
            }
            else if (string.Equals(arg, "--quiet", StringComparison.Ordinal))
            {
                quiet = true;
            }
            else if (!string.Equals(arg, "scan", StringComparison.Ordinal)
                     && raiz is null
                     && !arg.StartsWith('-'))
            {
                raiz = arg;
            }
            else
            {
                return Operacional($"uso invalido: '{arg}'. sintaxe: conflictdoctor scan <caminho> --json --quiet");
            }
        }

        if (raiz is null)
        {
            return Operacional("uso invalido: caminho obrigatorio. sintaxe: conflictdoctor scan <caminho> --json --quiet");
        }

        if (!Directory.Exists(raiz))
        {
            var pai = Path.GetDirectoryName(Path.GetFullPath(raiz));
            return Operacional(Directory.Exists(pai)
                ? $"raiz nao e um diretorio: {raiz}"
                : $"raiz inexistente: {raiz}");
        }

        try
        {
            return ExecutarScan(raiz, json, quiet);
        }
        catch (OperationCanceledException)
        {
            return Operacional("scan cancelado");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlaceholderViolationException or PlaceholderReadException)
        {
            // Falha fechada: erro operacional com mensagem única — nada parcial sem registro.
            return Operacional($"falha no scan: {ex.Message}");
        }
    }

    private static CliResult ExecutarScan(string raiz, bool json, bool quiet)
    {
        var iniciou = DateTimeOffset.UtcNow;

        // ---- L0 único (sidecar-aware): fonte da verdade de arquivos, erros e placeholders
        var l0 = DefaultEnumeration(raiz);

        // ---- L1→L3 sobre a MESMA lista já marcada e ordenada ---------------------------
        // O pipeline reordena internamente (defesa estrutural) e sua projeção consulta
        // PlaceholderPolicy.Classify — endurecido no T16 para tratar a marcação feita na
        // ORIGEM (sidecar T04 hoje, hook nativo amanhã) como autoridade máxima. Assim a
        // marca de placeholder simulado sobrevive ao wrap interno e NENHUM byte de
        // placeholder é lido (PLH-01), sem tradução artificial de bits.
        var resultado = new ScanPipeline(
            new ReplayMarkedEnumerator(l0.Files),
            new PlaceholderGuardedHasher(new Blake3Hasher()),
            new FileStreamSource()).Run(raiz);

        // ---- telemetria exata: contadores derivados das decisões registradas ------------
        // Parcial (L2) rodou para todo membro de grupo com 2+ itens; completo (L3) só
        // para membros dos grupos com veredito. Receita de bytes do ADR-0005 via
        // constantes do hasher (nunca números soltos).
        var membrosL2 = resultado.Groups
            .Where(g => g.Members.Count >= 2)
            .SelectMany(g => g.Members)
            .ToArray();
        // L3 cobre TODOS os membros dos grupos com veredito; o size do membro de um
        // conflito é o size do grupo (agrupamento é por size — Grouping.Group).
        var tamanhosL3 = resultado.IdenticalDuplicates
            .SelectMany(d => d.Files.Select(f => f.Size))
            .Concat(resultado.RealConflicts.SelectMany(c => c.Files.Select(_ => c.SizeBytes)))
            .ToArray();

        var telemetry = l0.Telemetry with
        {
            FilesSkipped = l0.Errors.Count,
            FilesPartialHashed = membrosL2.Length,
            FilesFullHashed = tamanhosL3.Length,
            BytesReadPartial = membrosL2.Sum(BytesParciais),
            BytesReadFull = tamanhosL3.Sum(),
        };

        var terminou = DateTimeOffset.UtcNow;
        var placeholders = PlaceholderReport.Records(l0.Files);

        // ---- veredito em exit code estável (SPEC §14) ------------------------------------
        var anomalias = resultado.IdenticalDuplicates.Count + resultado.RealConflicts.Count;
        var pulados = l0.Errors.Count;

        int exitCode;
        if (anomalias > 0 && pulados > 0)
        {
            exitCode = Program.ExitCodes.Partial;
        }
        else if (anomalias > 0)
        {
            exitCode = Program.ExitCodes.AnomaliesFound;
        }
        else
        {
            exitCode = Program.ExitCodes.Clean;
        }

        if (json)
        {
            using var saida = new MemoryStream();
            new ReportWriterJson().Write(resultado, telemetry, placeholders, raiz, iniciou, terminou, saida);
            return new CliResult(exitCode, System.Text.Encoding.UTF8.GetString(saida.ToArray()), null);
        }

        return new CliResult(exitCode, null, quiet
            ? string.Empty
            : FormatTexto(resultado, placeholders, l0.Errors, exitCode));
    }

    /// <summary>Receita v1 do ADR-0005: ≤ WholeFileLimitBytes lê o inteiro; acima,
    /// as duas janelas de WindowBytes numa única abertura.</summary>
    private static long BytesParciais(FileEntry f) =>
        f.Size <= Blake3Hasher.WholeFileLimitBytes
            ? f.Size
            : 2L * Blake3Hasher.WindowBytes;

    private static CliResult Operacional(string mensagem) =>
        new(Program.ExitCodes.OperationalError, null, mensagem);

    private static string FormatTexto(
        ScanResult r,
        IReadOnlyList<PlaceholderRecord> placeholders,
        IReadOnlyList<ScanError> erros,
        int exitCode)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"scan concluido: {r.Groups.Count} grupo(s) candidato(s)");

        foreach (var dup in r.IdenticalDuplicates)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"duplicata identica [{dup.SizeBytes} B] hash {dup.Hash[..12]}…");
            foreach (var f in dup.Files)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.Path}");
            }
        }

        foreach (var c in r.RealConflicts)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"conflito real: {c.NormalizedBaseName} [{c.SizeBytes} B]");
            foreach (var m in c.Files)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {m.Path} (hash {m.Hash[..12]}…)");
            }
        }

        if (placeholders.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"placeholders listados (nao lidos): {placeholders.Count}");
        }

        if (erros.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"arquivos pulados por erro de acesso: {erros.Count}");
            foreach (var erro in erros)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {erro.Path} ({erro.Message})");
            }
        }

        if (exitCode == Program.ExitCodes.Partial)
        {
            sb.AppendLine("STATUS: PARCIAL — anomalias encontradas e houve arquivos pulados.");
        }

        return sb.ToString().TrimEnd();
    }
}
