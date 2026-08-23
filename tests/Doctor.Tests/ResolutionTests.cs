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

    // ---------------------------------------------------------------- KeepLargest

    [Fact]
    public void KeepLargest_SizesDistintos_VenceMaior_MotivoAuditavel()
    {
        var grupo = new ConflictGroup("planilha", 0,
        [
            Entrada("/root/planilha.xlsx", 300, "2026-08-20T10:00:00Z"),
            Entrada("/root/planilha (1).xlsx", 500, "2026-08-19T09:00:00Z"), // maior
            Entrada("/root/planilha~.xlsx", 400, "2026-08-21T08:00:00Z"),
        ]);

        var plano = Resolution.Resolve(grupo, new KeepLargest());

        Assert.Equal("/root/planilha (1).xlsx", plano.Winner.Path);
        Assert.Equal(2, plano.Sacrifices.Count);
        Assert.All(plano.Sacrifices, s => Assert.Equal("keep-largest", s.Reason));
    }

    // ---------------------------------------------------------------- KeepMachine

    [Fact]
    public void KeepMachine_MarcaDesktopNoNome_VenceVersaoDaMaquinaPedida()
    {
        var grupo = new ConflictGroup("contrato", 0,
        [
            Entrada("/root/contrato-DESKTOP-ABC123.txt", 100, "2026-08-22T12:00:00Z"),
            Entrada("/root/contrato-DESKTOP-XYZ789.txt", 100, "2026-08-20T10:00:00Z"),
            Entrada("/root/contrato (1).txt", 100, "2026-08-21T11:00:00Z"), // sem marca
        ]);

        var plano = Resolution.Resolve(grupo, new KeepMachine("XYZ789"));

        Assert.Equal("/root/contrato-DESKTOP-XYZ789.txt", plano.Winner.Path);
        Assert.Equal(
            new[] { "/root/contrato (1).txt", "/root/contrato-DESKTOP-ABC123.txt" },
            plano.Sacrifices.Select(s => s.Entry.Path).ToArray());
        Assert.All(plano.Sacrifices, s => Assert.Equal("keep-machine:XYZ789", s.Reason));
    }

    // ---------------------------------------------------------------- KeepManual

    [Fact]
    public void KeepManual_EscolhaDentroDoGrupo_VenceEscolhaDoUsuario()
    {
        var grupo = new ConflictGroup("apostila", 0,
        [
            Entrada("/root/apostila.pdf", 100, "2026-08-22T12:00:00Z"), // mais recente
            Entrada("/root/apostila (1).pdf", 100, "2026-08-20T10:00:00Z"),
        ]);

        var plano = Resolution.Resolve(grupo, new KeepManual("/root/apostila (1).pdf"));

        Assert.Equal("/root/apostila (1).pdf", plano.Winner.Path);
        Assert.Equal(
            new[] { "/root/apostila.pdf" },
            plano.Sacrifices.Select(s => s.Entry.Path).ToArray());
        Assert.All(plano.Sacrifices, s => Assert.Equal("keep-manual", s.Reason));
    }

    [Fact]
    public void KeepManual_EscolhaForaDoGrupo_FalhaFechada()
    {
        var grupo = new ConflictGroup("apostila", 0,
        [
            Entrada("/root/apostila.pdf", 100, "2026-08-22T12:00:00Z"),
            Entrada("/root/apostila (1).pdf", 100, "2026-08-20T10:00:00Z"),
        ]);

        Assert.Throws<InvalidOperationException>(
            () => Resolution.Resolve(grupo, new KeepManual("/outra/arquivo.pdf")));
    }

    // ---------------------------------------------------------------- Empates

    [Fact]
    public void EmpateTriplo_MtimesIdenticos_DesempataPorSizeDepoisPathByteWise()
    {
        // mtimes IDÊNTICOS: decisão nunca pode ser first-seen (SPEC §17; ADR-0003).
        var mtimeComum = "2026-08-22T12:00:00Z";
        var grupo = new ConflictGroup("inventario", 0,
        [
            Entrada("/root/inventario-B.txt", 200, mtimeComum),
            Entrada("/root/inventario-A.txt", 300, mtimeComum), // vence: maior size
            Entrada("/root/inventario-C.txt", 100, mtimeComum),
        ]);

        var plano = Resolution.Resolve(grupo, new KeepNewest());

        Assert.Equal("/root/inventario-A.txt", plano.Winner.Path);
        Assert.Equal(
            new[] { "/root/inventario-B.txt", "/root/inventario-C.txt" },
            plano.Sacrifices.Select(s => s.Entry.Path).ToArray());
    }

    [Fact]
    public void EmpateAbsoluto_MtimeSizeIguais_VenceMenorCaminhoByteWise()
    {
        var mtimeComum = "2026-08-22T12:00:00Z";
        long sizeComum = 300;
        var grupo = new ConflictGroup("inventario", 0,
        [
            Entrada("/root/inventario-b.txt", sizeComum, mtimeComum),
            Entrada("/root/inventario-a.txt", sizeComum, mtimeComum),
        ]);

        var plano = Resolution.Resolve(grupo, new KeepLargest());

        // path CRESCENTE byte-wise: "a" < "b" em Ordinal.
        Assert.Equal("/root/inventario-a.txt", plano.Winner.Path);
        Assert.Equal(
            new[] { "/root/inventario-b.txt" },
            plano.Sacrifices.Select(s => s.Entry.Path).ToArray());
    }
}
