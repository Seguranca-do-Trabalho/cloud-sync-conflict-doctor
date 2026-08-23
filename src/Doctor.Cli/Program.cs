namespace Doctor.Cli;

/// <summary>
/// T16 — ponto de entrada da CLI (SPEC §14). A camada de processo apenas converte
/// <see cref="Environment.ExitCode"/>; toda a decisão testável vive em
/// <see cref="ScanCommand"/>, que retorna o exit code documentado:
/// 0 sem anomalias, 1 erro operacional, 2 duplicatas/conflitos, 3 parcial.
/// </summary>
internal static class Program
{
    /// <summary>Códigos de saída estáveis e documentados (SPEC §14; EPIC 09).</summary>
    internal static class ExitCodes
    {
        /// <summary>Scan concluído: nenhum grupo candidato, duplicata ou conflito.</summary>
        public const int Clean = 0;

        /// <summary>Erro operacional: uso inválido, raiz inexistente ou falha do scan.</summary>
        public const int OperationalError = 1;

        /// <summary>Scan concluído: duplicatas idênticas e/ou conflitos reais encontrados.</summary>
        public const int AnomaliesFound = 2;

        /// <summary>Scan parcialmente concluído: anomalias presentes E arquivos pulados.</summary>
        public const int Partial = 3;
    }

    internal static int Main(string[] args)
    {
        // Contrato mínimo do card T16: 'conflictdoctor scan <path> [--json] [--quiet]'.
        if (args.Length == 0)
        {
            Console.Error.WriteLine("uso: conflictdoctor scan <path> [--json] [--quiet]");
            return ExitCodes.OperationalError;
        }

        var resultado = ScanCommand.Run(args);

        // SPEC §14: modo JSON é consumido por scripts/RMM — o relatório v1 vai SEMPRE
        // ao stdout. Texto humano vai ao stdout; mensagem operacional (exit 1) vai ao
        // stderr. --quiet com sucesso/anomalia imprime nada (silêncio é o contrato).
        if (resultado.JsonOutput is not null)
        {
            Console.Out.WriteLine(resultado.JsonOutput);
        }
        else if (!string.IsNullOrEmpty(resultado.HumanText))
        {
            var destino = resultado.ExitCode == ExitCodes.OperationalError ? Console.Error : Console.Out;
            destino.WriteLine(resultado.HumanText);
        }

        return resultado.ExitCode;
    }
}
