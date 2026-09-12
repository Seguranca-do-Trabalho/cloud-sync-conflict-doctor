namespace Doctor.Core;

/// <summary>
/// Binary fallback comparator (SPEC §16; ADR-0011 item 1 — unknown or missing
/// extension ⇒ binary; contracts.md IDocumentComparator). Fixed 64 KiB block
/// reads; first divergence ⇒ <see cref="RegionKind.Changed"/> with
/// LeftStart=RightStart=byte offset and counts 1. Common prefix equal with
/// different sizes ⇒ excess as <see cref="RegionKind.Removed"/> (left only) or
/// <see cref="RegionKind.Added"/> (right only), from the end of the common
/// span. Identical ⇒ AreSemanticallyEqual=true, empty Regions. Byte-by-byte
/// comparison, no normalization. Inherited gate (ADR-0011 item 4): placeholder
/// throws <see cref="PlaceholderReadException"/> BEFORE any opening.
/// </summary>
public sealed class BinaryFallbackComparator : IDocumentComparator
{
    private const int BlockSize = 64 * 1024;

    /// <inheritdoc cref="IDocumentComparator.Compare"/>
    public ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        GatePlaceholder(left);
        GatePlaceholder(right);

        using var streamL = File.OpenRead(left.Path);
        using var streamR = File.OpenRead(right.Path);
        var bufferL = new byte[BlockSize];
        var bufferR = new byte[BlockSize];
        long baseOffset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int readL = ReadBlock(streamL, bufferL);
            int readR = ReadBlock(streamR, bufferR);
            int common = Math.Min(readL, readR);
            for (int i = 0; i < common; i++)
            {
                if (bufferL[i] != bufferR[i])
                {
                    long offset = baseOffset + i;
                    return new ComparisonResult(
                        "binary",
                        false,
                        new[] { new DiffRegion(RegionKind.Changed, (int)offset, 1, (int)offset, 1) });
                }
            }

            if (readL != readR)
            {
                // End of one file within a block with common prefix:
                // excess is a unilateral region at the current offset.
                long offset = baseOffset + common;
                int excess = Math.Abs(readL - readR);
                var kind = readL > readR ? RegionKind.Removed : RegionKind.Added;
                var region = kind == RegionKind.Removed
                    ? new DiffRegion(kind, (int)offset, excess, (int)offset, 0)
                    : new DiffRegion(kind, (int)offset, 0, (int)offset, excess);
                return new ComparisonResult("binary", false, new[] { region });
            }

            if (readL == 0)
            {
                return new ComparisonResult("binary", true, Array.Empty<DiffRegion>());
            }

            baseOffset += readL;
        }
    }

    /// <summary>Reads until the block is full (File.Read may return fewer than requested).</summary>
    private static int ReadBlock(FileStream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void GatePlaceholder(FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }
    }
}
