namespace Doctor.Core;

/// <summary>
/// Motor de diff por LCS sobre sequências de linhas (SPEC §16; ADR-0011 item 5 —
/// implementação própria ~Hirschberg, espaço linear O(n+m), tempo O(n·m), sem
/// dependência externa; §52). Comparação de linhas exata pos-normalização
/// (<see cref="StringComparison.Ordinal"/>, sem trim).
///
/// Saída: lista de <see cref="DiffRegion"/> que PARTICIONA os dois documentos em
/// ordem — soma(LeftCount) == left.Count, soma(RightCount) == right.Count,
/// posições crescentes e não sobrepostas. Blocos de substituição emitem
/// <see cref="RegionKind.Changed"/> pareando 1:1 as primeiras min(d,r) linhas,
/// seguidos de <see cref="RegionKind.Removed"/>/<see cref="RegionKind.Added"/>
/// para o excedente. Determinístico: mesma entrada ⇒ mesma saída (desempate da
/// divisão de Hirschberg sempre pelo menor índice).
/// </summary>
public static class LcsDiff
{
    private const byte Manter = 0;
    private const byte Remover = 1;
    private const byte Inserir = 2;

    /// <summary>Calcula as regiões de diff entre <paramref name="left"/> e <paramref name="right"/>.</summary>
    public static List<DiffRegion> Diff(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var ops = new List<byte>(left.Count + right.Count);
        Alinhar(left, right, 0, left.Count, 0, right.Count, ops);
        return ParaRegioes(ops);
    }

    /// <summary>
    /// Divide &amp; conquista de Hirschberg: corta <paramref name="left"/> ao meio,
    /// acha o corte ótimo de <paramref name="right"/> por duas passadas de DP com
    /// uma linha cada e recursa. Casos-base resolvem diretamente (sem alocação de
    /// matriz n·m). Desempate do corte sempre pelo menor índice ⇒ saída estável.
    /// </summary>
    private static void Alinhar(
        IReadOnlyList<string> a, IReadOnlyList<string> b,
        int al, int ar, int bl, int br,
        List<byte> ops)
    {
        if (al == ar)
        {
            for (int k = bl; k < br; k++)
            {
                ops.Add(Inserir);
            }

            return;
        }

        if (bl == br)
        {
            for (int k = al; k < ar; k++)
            {
                ops.Add(Remover);
            }

            return;
        }

        if (ar - al == 1)
        {
            string linha = a[al];
            int k = bl;
            while (k < br && !string.Equals(b[k], linha, StringComparison.Ordinal))
            {
                k++;
            }

            for (int x = bl; x < k; x++)
            {
                ops.Add(Inserir);
            }

            if (k < br)
            {
                ops.Add(Manter);
                for (int x = k + 1; x < br; x++)
                {
                    ops.Add(Inserir);
                }
            }
            else
            {
                // Sem casamento: o pré-loop já emitiu TODAS as linhas de b como
                // Inserir; falta apenas remover a única linha de a.
                ops.Add(Remover);
            }

            return;
        }

        if (br - bl == 1)
        {
            string linha = b[bl];
            int j = al;
            while (j < ar && !string.Equals(a[j], linha, StringComparison.Ordinal))
            {
                j++;
            }

            for (int x = al; x < j; x++)
            {
                ops.Add(Remover);
            }

            if (j < ar)
            {
                ops.Add(Manter);
                for (int x = j + 1; x < ar; x++)
                {
                    ops.Add(Remover);
                }
            }
            else
            {
                // Sem casamento: o pré-loop já emitiu TODAS as linhas de a como
                // Remover; falta apenas inserir a única linha de b.
                ops.Add(Inserir);
            }

            return;
        }

        int meio = al + ((ar - al) >> 1);
        int m = br - bl;
        var frente = LinhaLcsFrente(a, al, meio, b, bl, br);
        var tras = LinhaLcsTras(a, meio, ar, b, bl, br);

        int melhor = -1;
        int corte = bl;
        for (int x = 0; x <= m; x++)
        {
            int soma = frente[x] + tras[x];
            if (soma > melhor)
            {
                melhor = soma;
                corte = bl + x;
            }
        }

        Alinhar(a, b, al, meio, bl, corte, ops);
        Alinhar(a, b, meio, ar, corte, br, ops);
    }

    /// <summary>
    /// Linha de DP: resultado[x] = |LCS(a[ai,af), b[bi,bi+x))|. Uma única linha de
    /// tamanho m+1 viva por chamada — é daqui que vem o espaço linear.
    /// </summary>
    private static int[] LinhaLcsFrente(IReadOnlyList<string> a, int ai, int af, IReadOnlyList<string> b, int bi, int bf)
    {
        int m = bf - bi;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int i = ai; i < af; i++)
        {
            string linha = a[i];
            cur[0] = 0;
            for (int x = 1; x <= m; x++)
            {
                cur[x] = string.Equals(linha, b[bi + x - 1], StringComparison.Ordinal)
                    ? prev[x - 1] + 1
                    : Math.Max(prev[x], cur[x - 1]);
            }

            (prev, cur) = (cur, prev);
        }

        return prev;
    }

    /// <summary>
    /// Espelho reverso da DP: resultado[x] = |LCS(a[ai,af), b[bi+x, bf))|.
    /// Permite casar as duas metades no corte ótimo sem segunda matriz n·m.
    /// </summary>
    private static int[] LinhaLcsTras(IReadOnlyList<string> a, int ai, int af, IReadOnlyList<string> b, int bi, int bf)
    {
        int m = bf - bi;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int i = af - 1; i >= ai; i--)
        {
            string linha = a[i];
            cur[m] = 0;
            for (int x = m - 1; x >= 0; x--)
            {
                cur[x] = string.Equals(linha, b[bi + x], StringComparison.Ordinal)
                    ? prev[x + 1] + 1
                    : Math.Max(prev[x], cur[x + 1]);
            }

            (prev, cur) = (cur, prev);
        }

        return prev;
    }

    /// <summary>
    /// Converte a sequência de operações em regiões particionadas. Cada bloco máximo
    /// de operações não-Manter vira até três regiões: <see cref="RegionKind.Changed"/>
    /// (par 1:1 das primeiras min(d,i) linhas), depois o excedente como
    /// <see cref="RegionKind.Removed"/> ou <see cref="RegionKind.Added"/>.
    /// </summary>
    private static List<DiffRegion> ParaRegioes(List<byte> ops)
    {
        var regioes = new List<DiffRegion>();
        int li = 0, ri = 0, i = 0;
        while (i < ops.Count)
        {
            if (ops[i] == Manter)
            {
                int inicio = i;
                while (i < ops.Count && ops[i] == Manter)
                {
                    i++;
                }

                int c = i - inicio;
                regioes.Add(new DiffRegion(RegionKind.Equal, li, c, ri, c));
                li += c;
                ri += c;
                continue;
            }

            int leftInicio = li, rightInicio = ri, dels = 0, ins = 0;
            while (i < ops.Count && ops[i] != Manter)
            {
                if (ops[i] == Remover)
                {
                    dels++;
                    li++;
                }
                else
                {
                    ins++;
                    ri++;
                }

                i++;
            }

            int p = Math.Min(dels, ins);
            if (p > 0)
            {
                regioes.Add(new DiffRegion(RegionKind.Changed, leftInicio, p, rightInicio, p));
            }

            if (dels > p)
            {
                regioes.Add(new DiffRegion(RegionKind.Removed, leftInicio + p, dels - p, rightInicio + p, 0));
            }
            else if (ins > p)
            {
                regioes.Add(new DiffRegion(RegionKind.Added, leftInicio + p, 0, rightInicio + p, ins - p));
            }
        }

        return regioes;
    }
}
