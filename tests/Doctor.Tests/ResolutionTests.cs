namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T14 (t_83b9d360) — motor de resolução (SPEC §17): ResolutionPlan determinístico por
/// grupo do ScanPipeline, estratégias KeepNewest/KeepLargest/KeepMachine/KeepManual.
/// NENHUMA operação de arquivo: só o PLANO (vencedor + sacrificados). Desempate
/// obrigatório mtime → size → path byte-wise (ADR-0003), nunca first-seen.
/// Entradas sintéticas sem filesystem real: FileEntry é metadado imutável (Level 0).
/// </summary>
public sealed class ResolutionTests
{
    private static FileEntry Entrada(string caminho, long size, string mtimeIso) => new()
    {
        Path = caminho,
        Size = size,
        MtimeUtc = DateTimeOffset.Parse(mtimeIso, styles: System.Globalization.DateTimeStyles.AssumeUniversal),
        Attributes = FileAttributes.Normal,
        VolumeId = "t14-volume",
        FileId = caminho,
    };

    // ---------------------------------------------------------------- KeepNewest

    [Fact]
    public void KeepNewest_MtimesDistintos_VenceMaisRecente_SacrificadosEmOrdemCanonica()
    {
        var grupo = new ConflictGroup("relatorio", 100,
        [
            Entrada("/root/relatorio.txt", 100, "2026-08-20T10:00:00Z"),
            Entrada("/root/relatorio (1).txt", 100, "2026-08-22T12:00:00Z"), // mais recente
            Entrada("/root/relatorio-DESKTOP-ABC123.txt", 100, "2026-08-21T11:00:00Z"),
        ]);

        var plano = Resolution.Resolve(grupo, new KeepNewest());

        Assert.Equal("/root/relatorio (1).txt", plano.Winner.Path);
        Assert.Equal(
            new[] { "/root/relatorio-DESKTOP-ABC123.txt", "/root/relatorio.txt" },
            plano.Sacrifices.Select(s => s.Entry.Path).ToArray());
    }
}
