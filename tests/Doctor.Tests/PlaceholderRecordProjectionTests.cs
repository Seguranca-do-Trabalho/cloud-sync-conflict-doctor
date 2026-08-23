namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — projeção Placeholders[] do relatório (schema-report-v1.md §6.3):
/// toda entrada marcada como placeholder, ordenada por bytes de caminho, com rótulos
/// sem duplicatas na ordem canônica declarada (reparse_point → recall_on_data_access →
/// recall_on_open → offline) e tamanho obtido por metadados (nunca por abertura).
/// </summary>
public class PlaceholderRecordProjectionTests
{
    private static FileEntry Entrada(string path, long size, FileAttributes attrs)
    {
        var baseEntry = new FileEntry
        {
            Path = path,
            Size = size,
            MtimeUtc = DateTimeOffset.UnixEpoch,
            Attributes = attrs,
            VolumeId = "vol-test",
            FileId = "1",
        };

        // Marcação idêntica à do Level 0 (OrderedFileEnumerator/CrossPlatformEnumerator).
        return baseEntry with
        {
            IsPlaceholder = PlaceholderPolicy.IsPlaceholder(baseEntry),
            PlaceholderKind = PlaceholderPolicy.Classify(baseEntry),
        };
    }

    [Fact]
    public void Rotulos_NaOrdemCanonicaDeclarada_SemDuplicatas()
    {
        // Todos os quatro bits: ordem do schema §6.3, não a de precedência da policy.
        var todos = (FileAttributes.Offline
            | (FileAttributes)0x00040000 // RECALL_ON_OPEN
            | (FileAttributes)0x00400000 // RECALL_ON_DATA_ACCESS
            | FileAttributes.ReparsePoint);

        var registros = PlaceholderReport.Records([Entrada("p/x.bin", 7, todos)]);

        var unico = Assert.Single(registros);
        Assert.Equal(["reparse_point", "recall_on_data_access", "recall_on_open", "offline"], unico.Kinds);
        Assert.Equal(7, unico.SizeBytes);
    }

    [Fact]
    public void Rotulos_Parciais_CadaBitComSeuRotulo()
    {
        var registros = PlaceholderReport.Records(
        [
            Entrada("p/a.bin", 1, FileAttributes.ReparsePoint),
            Entrada("p/b.bin", 2, (FileAttributes)0x00400000), // RECALL_ON_DATA_ACCESS
            Entrada("p/c.bin", 3, (FileAttributes)0x00040000), // RECALL_ON_OPEN
            Entrada("p/d.bin", 4, FileAttributes.Offline),
        ]);

        Assert.Equal(
            ["reparse_point"],
            registros.Single(r => r.Path == "p/a.bin").Kinds);
        Assert.Equal(
            ["recall_on_data_access"],
            registros.Single(r => r.Path == "p/b.bin").Kinds);
        Assert.Equal(
            ["recall_on_open"],
            registros.Single(r => r.Path == "p/c.bin").Kinds);
        Assert.Equal(
            ["offline"],
            registros.Single(r => r.Path == "p/d.bin").Kinds);
    }

    [Fact]
    public void Lista_OrdenadaPorBytesDeCaminho_SomentePlaceholders()
    {
        var normal = Entrada("t/zz.txt", 9, FileAttributes.Normal); // nunca entra

        var registros = PlaceholderReport.Records(
        [
            Entrada("t/video.mp4", 100, FileAttributes.Offline),
            normal,
            Entrada("t/arquivo morto.docx", 50, (FileAttributes)0x00040000),
        ]);

        Assert.Equal(2, registros.Count);
        // Bytes UTF-8: ' ' (0x20) < 'v' — "arquivo..." antes de "video..."
        Assert.Equal("t/arquivo morto.docx", registros[0].Path);
        Assert.Equal("t/video.mp4", registros[1].Path);
    }

    [Fact]
    public void NaoPlaceholder_NaoGeraRegistro()
    {
        Assert.Empty(PlaceholderReport.Records([Entrada("t/a.txt", 1, FileAttributes.Normal)]));
    }
}
