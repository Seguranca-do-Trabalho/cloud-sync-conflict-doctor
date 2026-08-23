namespace Doctor.Core;

/// <summary>
/// Comparador de Markdown (SPEC §16 Markdown; ADR-0011 item 2; contratos.md
/// IDocumentComparator; decisões do orquestrador no card T24/t_f37457ba). Base:
/// diff textual linha a linha sobre <see cref="LcsDiff"/>. Camada estrutural LEVE
/// por cima — os ÚNICOS recursos reconhecidos são: linhas de cabeçalho ATX
/// (#{1..6} seguidos de espaço/fim de linha, na coluna 0) FORA de cercas ``` e o
/// equilíbrio de cercas. Sem AST, sem parser de markdown, sem dependência externa.
///
/// Regra de refinamento: região não-<see cref="RegionKind.Equal"/> cujo span
/// (left ou right) contém cabeçalho e cujo CONJUNTO de cabeçalhos difere entre
/// left/right ⇒ <see cref="RegionKind.Changed"/> preservando os spans originais
/// (mesmo que o LCS bruto tenha dito Added/Removed). Diferença somente de texto
/// comum permanece Added/Removed. Cabeçalhos dentro de cerca não contam; cerca
/// aberta sem fechamento engole o restante do documento (nenhum cabeçalho depois
/// dela conta). Determinismo byte-a-byte herdado do motor; sem campo de tempo.
///
/// Gate herdado (ADR-0011 item 4): placeholder ⇒ <see cref="PlaceholderReadException"/>
/// ANTES de qualquer abertura — zero bytes lidos de placeholder. O leitor de
/// linhas normalizadas é autocontido nesta classe (mesma semântica do
/// TextComparator, cujos membros privados não são tocados por este card).
/// </summary>
public sealed class MarkdownComparator : IDocumentComparator
{
    /// <inheritdoc cref="IDocumentComparator.Compare"/>
    public ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        GatePlaceholder(left);
        GatePlaceholder(right);

        var linhasLeft = LerLinhasNormalizadas(left.Path);
        var linhasRight = LerLinhasNormalizadas(right.Path);

        var regioes = LcsDiff.Diff(linhasLeft, linhasRight);
        var refinadas = RefinarPorCabecalhos(regioes, linhasLeft, linhasRight);

        bool iguais = refinadas.Count == 0
            || refinadas.All(r => r.Kind == RegionKind.Equal);
        return new ComparisonResult("markdown", iguais, refinadas);
    }

    private static void GatePlaceholder(FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }
    }

    /// <summary>
    /// Aplica a regra de refinamento: substitui por <see cref="RegionKind.Changed"/>
    /// cada região não-Equal cujo span contém cabeçalho em algum lado e cujos
    /// conjuntos de cabeçalhos divergem entre os lados. Demais regiões passam intactas.
    /// Os candidatos a cabeçalho são pré-computados por varredura GLOBAL do
    /// documento (o estado de cercas atravessa os limites de região — um span que
    /// começa no meio de uma cerca aberta herda esse estado).
    /// </summary>
    private static List<DiffRegion> RefinarPorCabecalhos(
        List<DiffRegion> regioes,
        List<string> linhasLeft,
        List<string> linhasRight)
    {
        var candidatosLeft = IndicesDeCabecalho(linhasLeft);
        var candidatosRight = IndicesDeCabecalho(linhasRight);

        var saida = new List<DiffRegion>(regioes.Count);
        foreach (var regiao in regioes)
        {
            if (regiao.Kind == RegionKind.Equal)
            {
                saida.Add(regiao);
                continue;
            }

            var cabLeft = CabecalhosNoSpan(linhasLeft, candidatosLeft, regiao.LeftStart, regiao.LeftCount);
            var cabRight = CabecalhosNoSpan(linhasRight, candidatosRight, regiao.RightStart, regiao.RightCount);

            bool spanTemCabecalho = cabLeft.Count > 0 || cabRight.Count > 0;
            if (spanTemCabecalho && !cabLeft.SetEquals(cabRight))
            {
                saida.Add(new DiffRegion(
                    RegionKind.Changed,
                    regiao.LeftStart, regiao.LeftCount,
                    regiao.RightStart, regiao.RightCount));
                continue;
            }

            saida.Add(regiao);
        }

        return saida;
    }

    /// <summary>
    /// Índices (globais) das linhas de cabeçalho do documento: varredura única onde
    /// cercas ``` alternam estado — cerca ABERTA e nunca fechada desliga o
    /// reconhecimento até o fim do documento.
    /// </summary>
    private static HashSet<int> IndicesDeCabecalho(List<string> linhas)
    {
        var indices = new HashSet<int>();
        bool emCerca = false;
        for (int i = 0; i < linhas.Count; i++)
        {
            string linha = linhas[i];
            if (EhLinhaDeCerca(linha))
            {
                emCerca = !emCerca;
                continue;
            }

            if (!emCerca && EhCabecalhoAtx(linha))
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    /// <summary>
    /// Conjunto (Ordinal) das linhas de cabeçalho no intervalo [inicio, inicio+contagem),
    /// restrito aos índices pré-computados como cabeçalho fora de cerca.
    /// </summary>
    private static HashSet<string> CabecalhosNoSpan(
        List<string> linhas, HashSet<int> indicesDeCabecalho, int inicio, int contagem)
    {
        var conjunto = new HashSet<string>(StringComparer.Ordinal);
        int fim = Math.Min(inicio + contagem, linhas.Count);
        for (int i = inicio; i < fim; i++)
        {
            if (indicesDeCabecalho.Contains(i))
            {
                conjunto.Add(linhas[i]);
            }
        }

        return conjunto;
    }

    /// <summary>Linha de cerca: começa (após espaços) com três crases.</summary>
    private static bool EhLinhaDeCerca(string linha) =>
        linha.TrimStart().StartsWith("```", StringComparison.Ordinal);

    /// <summary>Cabeçalho ATX estrito: 1 a 6 cerilhas na coluna 0, depois espaço, tab ou fim de linha.</summary>
    private static bool EhCabecalhoAtx(string linha)
    {
        int i = 0;
        while (i < linha.Length && linha[i] == '#')
        {
            i++;
        }

        return i is >= 1 and <= 6
            && (i == linha.Length || linha[i] == ' ' || linha[i] == '\t');
    }

    /// <summary>Lê o arquivo como UTF-8, remove BOM se presente e divide em linhas com fim LF (sem trim).</summary>
    private static List<string> LerLinhasNormalizadas(string path)
    {
        using var reader = new StreamReader(path);
        var texto = reader.ReadToEnd();
        if (texto.Length > 0 && texto[0] == '\uFEFF')
        {
            texto = texto[1..];
        }

        texto = texto.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (texto.Length == 0)
        {
            return [];
        }

        var linhas = new List<string>();
        int inicio = 0;
        for (int i = 0; i < texto.Length; i++)
        {
            if (texto[i] == '\n')
            {
                linhas.Add(texto[inicio..i]);
                inicio = i + 1;
            }
        }

        if (inicio < texto.Length)
        {
            linhas.Add(texto[inicio..]);
        }

        return linhas;
    }
}
