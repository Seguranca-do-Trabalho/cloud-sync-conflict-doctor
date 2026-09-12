namespace Doctor.Cli;

/// <summary>
/// Entry point (T16): parses argv, delegates to <see cref="ScanCommand"/>;
/// <see cref="Environment.ExitCode"/>; all testable decisions live in
/// <see cref="ScanCommand.Run"/>.
/// </summary>
public static class Program
{
    /// <summary>Stable, documented exit codes (SPEC §14; EPIC 09).</summary>
    internal static class ExitCodes
    {
        /// <summary>Scan completed: no candidate groups, duplicates, or conflicts.</summary>
        public const int Clean = 0;

        /// <summary>Operational error: invalid usage, nonexistent root, or scan failure.</summary>
        public const int OperationalError = 1;

        /// <summary>Scan completed: identical duplicates and/or real conflicts found.</summary>
        public const int AnomaliesFound = 2;

        /// <summary>Scan partially completed: anomalies present AND files skipped.</summary>
        public const int Partial = 3;
    }

    public static int Main(string[] args)
    {
        // Minimum contract of card T16: 'conflictdoctor scan <path> [--json] [--quiet]'.
        // For now, only 'scan' exists; future subcommands (quarantine, restore, diff)
        // will be added as new static methods without changing this entry point.

        var result = ScanCommand.Run(args);

        // SPEC §14: JSON mode is consumed by scripts/RMM — the v1 report ALWAYS goes
        // to stdout. Human text goes to stdout; operational message (exit 1) goes to
        // stderr. --quiet with success/anomaly prints nothing (silence is the contract).
        if (result.JsonOutput is not null)
        {
            Console.Write(result.JsonOutput);
        }
        else if (!string.IsNullOrEmpty(result.HumanText))
        {
            Console.Write(result.HumanText);
        }

        return result.ExitCode;
    }
}
