namespace Doctor.Core;

/// <summary>
/// Comparador binário de fallback (SPEC §16; ADR-0011 item 1 — extensão desconhecida
/// ou ausente ⇒ binário; contratos.md IDocumentComparator). Leitura em blocos fixos
/// de 64 KiB; primeira divergência ⇒ <see cref="RegionKind.Changed"/> com
/// LeftStart=RightStart=offset do byte e contagens 1. Prefixo comum igual com
/// tamanhos diferentes ⇒ excedente como <see cref="RegionKind.Removed"/> (somente
/// left) ou <see cref="RegionKind.Added"/> (somente right), a partir do fim do trecho
/// comum. Idênticos ⇒ AreSemanticallyEqual=true, Regions vazia. Comparação byte a
/// byte, sem normalização. Gate herdado (ADR-0011 item 4): placeholder lança
/// <see cref="PlaceholderReadException"/> ANTES de qualquer abertura.
/// </summary>
public sealed class BinaryFallbackComparator : IDocumentComparator
{
    private const int TamanhoBloco = 64 * 1024;

    /// <inheritdoc cref="IDocumentComparator.Compare"/>
    public ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        GatePlaceholder(left);
        GatePlaceholder(right);

        using var streamL = File.OpenRead(left.Path);
        using var streamR = File.OpenRead(right.Path);
        var bufferL = new byte[TamanhoBloco];
        var bufferR = new byte[TamanhoBloco];
        long offsetBase = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int lidosL = LerBloco(streamL, bufferL);
            int lidosR = LerBloco(streamR, bufferR);
            int comum = Math.Min(lidosL, lidosR);
            for (int i = 0; i < comum; i++)
            {
                if (bufferL[i] != bufferR[i])
                {
                    long offset = offsetBase + i;
                    return new ComparisonResult(
                        "binary",
                        false,
                        new[] { new DiffRegion(RegionKind.Changed, (int)offset, 1, (int)offset, 1) });
                }
            }

            if (lidosL != lidosR)
            {
                // Fim de um dos arquivos dentro de um bloco com prefixo igual:
                // o excedente é região unilateral no offset corrente.
                long offset = offsetBase + comum;
                int excedente = Math.Abs(lidosL - lidosR);
                var kind = lidosL > lidosR ? RegionKind.Removed : RegionKind.Added;
                var regiao = kind == RegionKind.Removed
                    ? new DiffRegion(kind, (int)offset, excedente, (int)offset, 0)
                    : new DiffRegion(kind, (int)offset, 0, (int)offset, excedente);
                return new ComparisonResult("binary", false, new[] { regiao });
            }

            if (lidosL == 0)
            {
                return new ComparisonResult("binary", true, Array.Empty<DiffRegion>());
            }

            offsetBase += lidosL;
        }
    }

    /// <summary>Lê até encher o bloco (File.Read pode devolver menos que o pedido).</summary>
    private static int LerBloco(FileStream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int lidos = stream.Read(buffer, total, buffer.Length - total);
            if (lidos == 0)
            {
                break;
            }

            total += lidos;
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
