using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_64c4d4c0) — Motor LCS sobre listas de linhas (SPEC §16; ADR-0011 item 5:
/// LCS próprio, sem dependência externa; §52 anti-overengineering).
///
/// Contrato de saída (decisão do orquestrador): sequência de <see cref="DiffRegion"/>
/// que PARTICIONA ambos os documentos em ordem — soma de LeftCount == left.Count,
/// soma de RightCount == right.Count, regiões em posições crescentes e não
/// sobrepostas. Equal cobre trechos casados; Added = somente right; Removed =
/// somente left; Changed = par substituto (uma linha removida pareada 1:1 com uma
/// linha adicionada no mesmo bloco).
/// </summary>
[Trait("Category", "Comparison")]
public class LcsDiffTests
{
    [Fact]
    public void Lcs01_AmbosVazios_NenhumaRegiao()
    {
        var regioes = LcsDiff.Diff([], []);

        Assert.Empty(regioes);
    }

    [Fact]
    public void Lcs02_Identicos_UmaUnicaRegiaoEqualCobrindoTudo()
    {
        var linhas = new[] { "a", "b", "c" };

        var regioes = LcsDiff.Diff(linhas, linhas);

        var regiao = Assert.Single(regioes);
        Assert.Equal(RegionKind.Equal, regiao.Kind);
        Assert.Equal(0, regiao.LeftStart);
        Assert.Equal(3, regiao.LeftCount);
        Assert.Equal(0, regiao.RightStart);
        Assert.Equal(3, regiao.RightCount);
    }

    [Fact]
    public void Lcs03_Prefixo_EqualSeguidoDeAdded()
    {
        var left = new[] { "a", "b" };
        var right = new[] { "a", "b", "c", "d" };

        var regioes = LcsDiff.Diff(left, right);

        Assert.Equal(2, regioes.Count);
        Assert.Equal(RegionKind.Equal, regioes[0].Kind);
        Assert.Equal((0, 2, 0, 2), (regioes[0].LeftStart, regioes[0].LeftCount, regioes[0].RightStart, regioes[0].RightCount));
        Assert.Equal(RegionKind.Added, regioes[1].Kind);
        Assert.Equal((2, 0, 2, 2), (regioes[1].LeftStart, regioes[1].LeftCount, regioes[1].RightStart, regioes[1].RightCount));
    }

    [Fact]
    public void Lcs04_Sufixo_AddedSeguidoDeEqual()
    {
        var left = new[] { "c", "d" };
        var right = new[] { "a", "b", "c", "d" };

        var regioes = LcsDiff.Diff(left, right);

        Assert.Equal(2, regioes.Count);
        Assert.Equal(RegionKind.Added, regioes[0].Kind);
        Assert.Equal((0, 0, 0, 2), (regioes[0].LeftStart, regioes[0].LeftCount, regioes[0].RightStart, regioes[0].RightCount));
        Assert.Equal(RegionKind.Equal, regioes[1].Kind);
        Assert.Equal((0, 2, 2, 2), (regioes[1].LeftStart, regioes[1].LeftCount, regioes[1].RightStart, regioes[1].RightCount));
    }

    [Fact]
    public void Lcs05_RemocaoPura_RegiaoRemoved()
    {
        var left = new[] { "a", "x", "b" };
        var right = new[] { "a", "b" };

        var regioes = LcsDiff.Diff(left, right);

        Assert.Equal(3, regioes.Count);
        Assert.Equal(RegionKind.Equal, regioes[0].Kind);
        Assert.Equal(RegionKind.Removed, regioes[1].Kind);
        Assert.Equal((1, 1, 1, 0), (regioes[1].LeftStart, regioes[1].LeftCount, regioes[1].RightStart, regioes[1].RightCount));
        Assert.Equal(RegionKind.Equal, regioes[2].Kind);
        Assert.Equal((2, 1, 1, 1), (regioes[2].LeftStart, regioes[2].LeftCount, regioes[2].RightStart, regioes[2].RightCount));
    }

    [Fact]
    public void Lcs06_Intercalado_SubstituicaoViraChanged()
    {
        var left = new[] { "linha1", "alfa", "linha3" };
        var right = new[] { "linha1", "beta", "linha3" };

        var regioes = LcsDiff.Diff(left, right);

        Assert.Equal(3, regioes.Count);
        Assert.Equal(RegionKind.Equal, regioes[0].Kind);
        Assert.Equal((0, 1, 0, 1), (regioes[0].LeftStart, regioes[0].LeftCount, regioes[0].RightStart, regioes[0].RightCount));
        Assert.Equal(RegionKind.Changed, regioes[1].Kind);
        Assert.Equal((1, 1, 1, 1), (regioes[1].LeftStart, regioes[1].LeftCount, regioes[1].RightStart, regioes[1].RightCount));
        Assert.Equal(RegionKind.Equal, regioes[2].Kind);
        Assert.Equal((2, 1, 2, 1), (regioes[2].LeftStart, regioes[2].LeftCount, regioes[2].RightStart, regioes[2].RightCount));
    }

    [Fact]
    public void Lcs07_SinteticoGrande_SeedFixa_ParticaoELcsContraOraculo()
    {
        // Gerador com semente FIXA (proibido Random sem seed).
        var rnd = new Random(42);
        var left = new List<string>(400);
        var right = new List<string>(400);
        for (int i = 0; i < 400; i++)
        {
            var linha = $"L{i:D4}";
            left.Add(linha);
            right.Add(rnd.Next(4) == 0 ? linha + "-mut" : linha);
        }

        // Inserções e deleções espúrias tornam o caso não-trivial.
        right.Insert(97, "inserida-97");
        right.Insert(233, "inserida-233");
        left.RemoveAt(151);

        var regioes = LcsDiff.Diff(left, right);

        // Invariante de partição: cobre os dois documentos por completo, em ordem.
        int somaLeft = 0, somaRight = 0, ultimoLeft = 0, ultimoRight = 0;
        foreach (var r in regioes)
        {
            Assert.Equal(ultimoLeft, r.LeftStart);
            Assert.Equal(ultimoRight, r.RightStart);
            ultimoLeft += r.LeftCount;
            ultimoRight += r.RightCount;
            somaLeft += r.LeftCount;
            somaRight += r.RightCount;
            if (r.Kind == RegionKind.Changed)
            {
                Assert.Equal(r.LeftCount, r.RightCount);
            }
        }

        Assert.Equal(left.Count, somaLeft);
        Assert.Equal(right.Count, somaRight);

        // Oráculo independente: DP clássica O(n·m) do COMPRIMENTO do LCS.
        int lcsEsperado = ComprimentoLcsOraculo(left, right);
        int iguais = regioes.Where(r => r.Kind == RegionKind.Equal).Sum(r => r.LeftCount);
        Assert.Equal(lcsEsperado, iguais);

        // Regiões Equal realmente casam linha a linha (Ordinal).
        foreach (var r in regioes.Where(r => r.Kind == RegionKind.Equal))
        {
            for (int k = 0; k < r.LeftCount; k++)
            {
                Assert.True(string.Equals(left[r.LeftStart + k], right[r.RightStart + k], StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void Lcs08_MesmaEntradaDuasExecucoes_ResultadoIdentico()
    {
        var left = new[] { "a", "x", "b", "y", "c" };
        var right = new[] { "a", "p", "b", "q", "c", "extra" };

        var primeira = LcsDiff.Diff(left, right);
        var segunda = LcsDiff.Diff(left, right);

        Assert.Equal(primeira.Count, segunda.Count);
        for (int i = 0; i < primeira.Count; i++)
        {
            Assert.Equal(primeira[i], segunda[i]);
        }
    }

    /// <summary>DP clássica de comprimento de LCS — oráculo independente do motor Hirschberg.</summary>
    private static int ComprimentoLcsOraculo(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (int i = 1; i <= a.Count; i++)
        {
            for (int j = 1; j <= b.Count; j++)
            {
                dp[i, j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        return dp[a.Count, b.Count];
    }
}
