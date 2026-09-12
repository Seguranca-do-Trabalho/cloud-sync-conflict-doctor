using Doctor.Core;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// Scanner telemetry contract (card T06, SPEC §10 + benchmark-harness §6):
/// a false enumerator produces per-file counters, the collector aggregates with Merge,
/// and the placeholder_bytes_read safety gate remains zero when no placeholder
/// byte is read.
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
            PlaceholderBytesRead = 0, // placeholders never have content accessed
        };

    [Fact]
    public void Merge_SumsPerFileCounters_AndKeepsInvariants()
    {
        // Fake enumerator: 4 entries — 1 placeholder, 1 skipped, 1 partial hash, 1 full hash.
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

        // Normal contract invariants (Telemetry.cs): file and byte partitioning.
        Assert.Equal(total.FilesEnumerated,
            total.FilesPlaceholder + total.FilesSkipped + total.FilesPartialHashed);
        Assert.True(total.FilesFullHashed <= total.FilesPartialHashed);
        Assert.Equal(total.BytesReadPartial + total.BytesReadFull, total.BytesRead);
        Assert.Equal(131072 + 262144 + 8192, total.BytesRead);
    }

    [Fact]
    public void Gate_PlaceholderBytesRead_IsZeroByDefault_AndSurvivesMergeWithoutViolation()
    {
        var a = new ScanTelemetry(); // default: gate at zero
        var b = FromFile(placeholder: true); // placeholder processed without reading anything

        Assert.Equal(0, a.PlaceholderBytesRead);
        Assert.Equal(0, b.PlaceholderBytesRead);
        Assert.Equal(0, a.Merge(b).PlaceholderBytesRead);
    }

    [Fact]
    public void Gate_PlaceholderBytesRead_Propagates_WhenAnyOperandViolates()
    {
        var violation = new ScanTelemetry { PlaceholderBytesRead = 512 };
        var clean = FromFile(placeholder: false, bytesReadFull: 1024);

        var merged = violation.Merge(clean);

        Assert.Equal(512, merged.PlaceholderBytesRead); // run would fail in harness §6
    }
}
