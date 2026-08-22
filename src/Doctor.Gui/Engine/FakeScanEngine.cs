using System.Text;

namespace Doctor.Gui.Engine;

/// <summary>
/// Motor falso: dados fixos de demonstração DEMO — nenhum acesso a filesystem.
/// Estrutura coerente com docs/adr/ADR-0003.md (schema v1): BLAKE3 explícito,
/// telemetria com placeholder_bytes_read == 0 e listas pré-ordenadas por caminho
/// em bytes UTF-8 (regra de determinismo §3).
/// </summary>
public sealed class FakeScanEngine : IScanEngine
{
    public ScanReport Scan(string rootPath, Action<int>? progress = null)
    {
        progress?.Invoke(0);

        var duplicates = new[]
        {
            new DuplicateGroup
            {
                Blake3Hash = "demo-a1b2c3d4e5f60718293a4b5c6d7e8f90",
                SizeBytes = 1_048_576,
                Files = Sorted("Relatorios/2026/copia-relatorio-anual.docx",
                               "Relatorios/2026/relatorio-anual (1).docx",
                               "Relatorios/2026/relatorio-anual.docx"),
            },
            new DuplicateGroup
            {
                Blake3Hash = "demo-b2c3d4e5f60718293a4b5c6d7e8f90a1",
                SizeBytes = 2_458_912,
                Files = Sorted("Fotos/viagem-2025/praia - copia.jpg",
                               "Fotos/viagem-2025/praia.jpg"),
            },
        };

        var conflicts = new[]
        {
            new ConflictGroup
            {
                BaseName = "Projetos/orcamento.xlsx",
                Versions =
                [
                    new ConflictVersion
                    {
                        Path = "Projetos/orcamento.xlsx",
                        SizeBytes = 88_412,
                        MtimeUtc = new DateTimeOffset(2026, 8, 10, 14, 32, 0, TimeSpan.Zero),
                        Blake3Hash = "demo-c3d4e5f60718293a4b5c6d7e8f90a1b2",
                    },
                    new ConflictVersion
                    {
                        Path = "Projetos/orcamento (Notebook-Office).xlsx",
                        SizeBytes = 91_077,
                        MtimeUtc = new DateTimeOffset(2026, 8, 12, 9, 15, 0, TimeSpan.Zero),
                        Blake3Hash = "demo-d4e5f60718293a4b5c6d7e8f90a1b2c3",
                    },
                    new ConflictVersion
                    {
                        Path = "Projetos/orcamento (DESKTOP-4K2F conflicted copy 2026-08-13).xlsx",
                        SizeBytes = 90_240,
                        MtimeUtc = new DateTimeOffset(2026, 8, 13, 18, 47, 0, TimeSpan.Zero),
                        Blake3Hash = "demo-e5f60718293a4b5c6d7e8f90a1b2c3d4",
                    },
                ],
            },
        };

        // Placeholders: NUNCA abertos. Motivos reais do Windows/OneDrive (§7).
        var placeholders = new[]
        {
            new PlaceholderFile
            {
                Path = "Arquivados/backup-antigo.pst",
                SizeBytes = 1_873_285_120,
                Reason = "FILE_ATTRIBUTE_OFFLINE",
            },
            new PlaceholderFile
            {
                Path = "Videos/apresentacao-final.mp4",
                SizeBytes = 512_450_112,
                Reason = "RECALL_ON_OPEN",
            },
        };

        var report = new ScanReport
        {
            RootPath = rootPath,
            Telemetry = new ScanTelemetry
            {
                FilesEnumerated = 1847 + placeholders.Length,
                FilesSkipped = 0,
                FilesPlaceholder = placeholders.Length,
                BytesRead = duplicates.Sum(d => d.SizeBytes * d.Files.Count)
                            + conflicts.SelectMany(c => c.Versions).Sum(v => v.SizeBytes),
                PlaceholderBytesRead = 0,
            },
            IdenticalDuplicates = duplicates,
            RealConflicts = conflicts,
            Placeholders = placeholders,
        };

        // Progresso fake determinístico: simula fases do pipeline sem I/O real.
        foreach (var step in new[] { 20, 45, 70, 90, 100 })
        {
            progress?.Invoke(step);
        }

        return report;
    }

    /// <summary>Ordenação por bytes UTF-8 do caminho (§3 / ADR-0003), nunca locale.</summary>
    private static IReadOnlyList<string> Sorted(params string[] files) =>
        files.OrderBy(f => Encoding.UTF8.GetBytes(f), Comparer<byte[]>.Create(
            (a, b) => a.AsSpan().SequenceCompareTo(b.AsSpan()))).ToList();
}
