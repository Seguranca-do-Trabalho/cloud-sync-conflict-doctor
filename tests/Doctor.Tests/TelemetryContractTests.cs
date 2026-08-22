using Doctor.Core;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// Contrato da telemetria do scanner (card T06, SPEC §10 + benchmark-harness §6):
/// um enumerador falso produz contadores por arquivo, o coletor agrega com Merge
/// e o gate de segurança placeholder_bytes_read permanece zero quando nenhum byte
/// de placeholder é lido.
/// </summary>
public class TelemetryContractTests
{
    private static ScanTelemetry FromFile(bool placeholder, long bytesReadPartial = 0, long bytesReadFull = 0)
        => new()
        {
            FilesEnumerated = 1,
            FilesSkipped = !placeholder && bytesReadPartial == 0 && bytesReadFull == 0 ? 1 : 0,
            FilesPlaceholder = placeholder ? 1 : 0,
            FilesPartialHashed = !placeholder && bytesReadPartial > 0 ? 1 : 0,
            FilesFullHashed = !placeholder && bytesReadFull > 0 ? 1 : 0,
            BytesReadPartial = bytesReadPartial,
            BytesReadFull = bytesReadFull,
            PlaceholderBytesRead = 0, // placeholders nunca têm conteúdo acessado
        };

    [Fact]
    public void Merge_SumsPerFileCounters_AndKeepsInvariants()
    {
        // Fake enumerator: 4 entradas — 1 placeholder, 1 skipped, 1 hash parcial, 1 hash completo.
        var perFile = new[]
        {
            FromFile(placeholder: true),
            FromFile(placeholder: false),
            FromFile(placeholder: false, bytesReadPartial: 4096 + 4096),
            FromFile(placeholder: false, bytesReadPartial: 131072, bytesReadFull: 262144),
        };

        var total = new ScanTelemetry();
        foreach (var t in perFile)
        {
            total = total.Merge(t);
        }

        Assert.Equal(4, total.FilesEnumerated);
        Assert.Equal(1, total.FilesSkipped);
        Assert.Equal(1, total.FilesPlaceholder);
        Assert.Equal(2, total.FilesPartialHashed);
        Assert.Equal(1, total.FilesFullHashed);

        // Invariantes normais do contrato (Telemetry.cs): partição de arquivos e de bytes.
        Assert.Equal(total.FilesEnumerated,
            total.FilesPlaceholder + total.FilesSkipped + total.FilesPartialHashed);
        Assert.True(total.FilesFullHashed <= total.FilesPartialHashed);
        Assert.Equal(total.BytesReadPartial + total.BytesReadFull, total.BytesRead);
        Assert.Equal(131072 + 262144 + 8192, total.BytesRead);
    }

    [Fact]
    public void Gate_PlaceholderBytesRead_IsZeroByDefault_AndSurvivesMergeWithoutViolation()
    {
        var a = new ScanTelemetry(); // default: gate em zero
        var b = FromFile(placeholder: true); // placeholder processado sem ler nada

        Assert.Equal(0, a.PlaceholderBytesRead);
        Assert.Equal(0, b.PlaceholderBytesRead);
        Assert.Equal(0, a.Merge(b).PlaceholderBytesRead);
    }

    [Fact]
    public void Gate_PlaceholderBytesRead_Propagates_WhenAnyOperandViolates()
    {
        var violacao = new ScanTelemetry { PlaceholderBytesRead = 512 };
        var limpo = FromFile(placeholder: false, bytesReadFull: 1024);

        var mesclado = violacao.Merge(limpo);

        Assert.Equal(512, mesclado.PlaceholderBytesRead); // rodada seria reprovada no harness §6
    }
}
