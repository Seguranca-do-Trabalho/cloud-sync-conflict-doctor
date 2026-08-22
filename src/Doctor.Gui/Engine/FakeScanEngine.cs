using System.Text;

namespace Doctor.Gui.Engine;

/// <summary>
/// Motor falso: dados fixos de demonstração DEMO — nenhum acesso a filesystem.
/// Estrutura em forma do schema v1 (docs/schema-report-v1.md): telemetria com os
/// 9 contadores e invariantes mantidas, listas pré-ordenadas por caminho em bytes
/// UTF-8 (§3). Cenários selecionáveis cobrem as bordas do card: nominal,
/// zero duplicatas, zero conflitos, só placeholders.
/// </summary>
public sealed class FakeScanEngine : IScanEngine
{
    /// <summary>Cenários DEMO selecionáveis para exercitar bordas da GUI.</summary>
    public enum ScanScenario
    {
        /// <summary>Duplicatas idênticas + conflito real + placeholders.</summary>
        Nominal,
        /// <summary>Borda: nenhuma classe de duplicata idêntica; conflitos e placeholders presentes.</summary>
        SemDuplicatas,
        /// <summary>Borda: nenhum grupo com divergência real; duplicatas e placeholders presentes.</summary>
        SemConflitos,
        /// <summary>Borda: apenas placeholders; nenhum byte de conteúdo é lido.</summary>
        SoPlaceholders,
    }

    private readonly ScanScenario _scenario;

    public FakeScanEngine(ScanScenario scenario = ScanScenario.Nominal) => _scenario = scenario;

    // Janela de hash parcial (Level 2): cabeça + cauda de 64 KiB.
    private const long JanelaParcialBytes = 64 * 1024;

    public ScanReport Scan(string rootPath, Action<int>? progress = null)
    {
        progress?.Invoke(0);
        var soPlaceholders = _scenario == ScanScenario.SoPlaceholders;

        var duplicates = _scenario == ScanScenario.SemDuplicatas || soPlaceholders
            ? []
            : new[]
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

        var conflitos = new[]
        {
            new ConflictGroup
            {
                BaseName = "Projetos/orcamento.xlsx",
                TotalBytes = 91_077,
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

        var conflicts = _scenario == ScanScenario.SemConflitos || soPlaceholders
            ? []
            : conflitos;


        // Placeholders: NUNCA abertos. Rótulos canônicos do schema v1 §6.3.
        var placeholdersBase = new[]
        {
            new PlaceholderFile
            {
                Path = "Arquivados/backup-antigo.pst",
                Kinds = ["offline"],
                SizeBytes = 1_873_285_120,
                Reason = "FILE_ATTRIBUTE_OFFLINE",
            },
            new PlaceholderFile
            {
                Path = "Videos/apresentacao-final.mp4",
                Kinds = ["recall_on_open"],
                SizeBytes = 512_450_112,
                Reason = "RECALL_ON_OPEN",
            },
        };
        var placeholders = placeholdersBase;

        var report = new ScanReport
        {
            RootPath = rootPath,
            Telemetry = CalcularTelemetry(duplicates, conflicts, placeholders, soPlaceholders),
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

    /// <summary>
    /// Telemetria derivada dos próprios dados DEMO — os contadores contam o que o
    /// cenário representa, não números soltos:
    /// enumerados = placeholders + únicos (skipped) + hasheados parcialmente;
    /// bytes lidos seguem as janelas do Level 2 (≤ 128 KiB: arquivo inteiro uma vez)
    /// e o arquivo inteiro dos sobreviventes no Level 3.
    /// </summary>
    private static ScanTelemetry CalcularTelemetry(
        IReadOnlyList<DuplicateGroup> duplicates,
        IReadOnlyList<ConflictGroup> conflicts,
        IReadOnlyList<PlaceholderFile> placeholders,
        bool soPlaceholders)
    {
        if (soPlaceholders)
        {
            // Cenário só de placeholders: nada além deles existe na árvore DEMO.
            return new ScanTelemetry
            {
                FilesEnumerated = placeholders.Count,
                FilesPlaceholder = placeholders.Count,
                FilesSkipped = 0,
                FilesPartialHashed = 0,
                FilesFullHashed = 0,
                BytesRead = 0,
                BytesReadPartial = 0,
                BytesReadFull = 0,
                PlaceholderBytesRead = 0,
            };
        }

        // Únicos fora de grupos: nunca hasheados → files_skipped (schema §5).
        const long unicosSkipped = 4;

        var versoesConteudo = duplicates.Sum(d => d.Files.Count)
                              + conflicts.SelectMany(c => c.Versions).Count();

        var partialHashed = versoesConteudo + 2; // +2 eliminados ainda no Level 2

        // Bytes do passe parcial: arquivos grandes pagam 2×64 KiB; pequenos (≤128 KiB),
        // o arquivo inteiro uma única vez.
        long bytesReadPartial = duplicates.Sum(g => ParcialDe(g.SizeBytes, g.Files.Count))
                                + conflicts.SelectMany(c => c.Versions)
                                           .Sum(v => ParcialDe(v.SizeBytes, 1));

        // Bytes do passe completo: apenas os sobreviventes ao Level 2 — que são
        // exatamente as versões presentes nas coleções DEMO (os 2 eliminados no L2
        // não figuram em grupo algum, só no contador files_partial_hashed).
        long bytesReadFull =
            duplicates.Sum(d => d.SizeBytes * d.Files.Count)
            + conflicts.SelectMany(c => c.Versions).Sum(v => v.SizeBytes);

        return new ScanTelemetry
        {
            FilesEnumerated = placeholders.Count + unicosSkipped + partialHashed,
            FilesSkipped = unicosSkipped,
            FilesPlaceholder = placeholders.Count,
            FilesPartialHashed = partialHashed,
            FilesFullHashed = versoesConteudo,
            BytesRead = bytesReadPartial + bytesReadFull,
            BytesReadPartial = bytesReadPartial,
            BytesReadFull = bytesReadFull,
            PlaceholderBytesRead = 0,
        };
    }

    /// <summary>Bytes do passe L2 por arquivo: min(tamanho, 64 KiB) × 2 (cabeça + cauda).</summary>
    private static long ParcialDe(long sizeBytes, int copias) =>
        copias * (Math.Min(sizeBytes, JanelaParcialBytes)
                  + Math.Min(sizeBytes - JanelaParcialBytes > 0
                      ? sizeBytes - JanelaParcialBytes
                      : 0,
                      JanelaParcialBytes));

    /// <summary>Ordenação por bytes UTF-8 do caminho (§3 / schema §1.3), nunca locale.</summary>
    private static IReadOnlyList<string> Sorted(params string[] files) =>
        files.OrderBy(f => Encoding.UTF8.GetBytes(f), Comparer<byte[]>.Create(
            (a, b) => a.AsSpan().SequenceCompareTo(b.AsSpan()))).ToList();
}
