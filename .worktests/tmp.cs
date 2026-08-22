using Doctor.Core;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// Teste unitário único do escopo reduzido T06: prova Merge para agregação e o
/// invariante do gate placeholder_bytes_read == 0 com um enumerador falso.
/// </summary>
public class TelemetryTests
{
    /// <summary>Enumerador falso: produz contagens por categoria sem tocar disco.</summary>
    private sealed class FakeEnumerator
    {
        public int Enumerated;
        public int Skipped;
        public int Placeholders;
        public int PartialHashed;
        public int FullHashed;
        public long PartialBytes;
        public long FullBytes;

        public ScanTelemetry Snapshot() => new()
        {
            FilesEnumerated = Interlocked.Read(ref Unsafe.As<int, long>(ref Enumerated)),
            FilesSkipped = Skipped,
            FilesPlaceholder = Placeholders,
            FilesPartialHashed = PartialHashed,
            FilesFullHashed = FullHashed,
            BytesReadPartial = PartialBytes,
            BytesReadFull = FullBytes,
        };
    }

    [Fact]
    public void Merge_e_gate_placeholder_zero_com_enumerador_falso()
    {
        // --- Arrange: dois scans falsos com categorias distintas ---
        var scan1 = new FakeEnumerator();
        var scan2 = new FakeEnumerator();

        var t1 = scan1.Snapshot() with
        {
            FilesEnumerated = 6, FilesSkipped = 2, FilesPartialHashed = 3, FilesFullHashed = 1,
            BytesReadPartial = 300, BytesReadFull = 700,
        };
        var t2 = scan2.Snapshot() with
        {
            FilesEnumerated = 4, FilesPlaceholder = 1, FilesPartialHashed = 3,
            BytesReadPartial = 200,
        };

        // --- Act: agregação dos dois shards ---
        var merged = t1.Merge(t2);

        // --- Assert: somas campo a campo ---
        Assert.Equal(10, merged.FilesEnumerated);
        Assert.Equal(2, merged.FilesSkipped);
        Assert.Equal(1, merged.FilesPlaceholder);
        Assert.Equal(6, merged.FilesPartialHashed);
        Assert.Equal(1, merged.FilesFullHashed);
        Assert.Equal(500, merged.BytesReadPartial);
        Assert.Equal(700, merged.BytesReadFull);
        Assert.Equal(1200, merged.BytesRead);          // derivado: partial + full

        // --- Gate: placeholder_bytes_read == 0 nos operandos e no agregado ---
        Assert.Equal(0, t1.PlaceholderBytesRead);
        Assert.Equal(0, t2.PlaceholderBytesRead);
        Assert.Equal(0, merged.PlaceholderBytesRead);

        // --- Identidade: merge com zero é o próprio valor ---
        Assert.Equal(t1, t1.Merge(new ScanTelemetry()));

        // --- Comutatividade das somas (record equality) ---
        Assert.Equal(t1.Merge(t2), t2.Merge(t1));
    }

    [Fact]
    public void Gate_violado_propaga_para_o_merge()
    {
        // Qualquer operando com gate != 0 contamina o agregado — reprovação da rodada.
        var bom = new ScanTelemetry { FilesEnumerated = 1, FilesPartialHashed = 1 };
        var ruim = new ScanTelemetry { PlaceholderBytesRead = 4096 };

        Assert.Equal(4096, ruim.Merge(bom).PlaceholderBytesRead);
    }
}
