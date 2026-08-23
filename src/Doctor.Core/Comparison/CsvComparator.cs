namespace Doctor.Core;

/// <summary>
/// Comparador de CSV (SPEC §16 CSV; ADR-0011 item 2; contratos.md
/// IDocumentComparator; decisões do orquestrador no card T24/t_f37457ba). Parsing
/// subconjunto RFC4180: campos entre aspas duplas, aspa escapada ""; delimitador
/// detectado por contagem na PRIMEIRA linha não-vazia de cada arquivo entre
/// ',' ';' '\t'; delimitadores DIFERENTES entre os arquivos ⇒ diferença
/// estrutural: TODAS as linhas em regiões <see cref="RegionKind.Changed"/>. Sem
/// inferência de header. Sem reordenação de linhas — ordem é significado.
///
/// Alinhamento por <see cref="LcsDiff"/> sobre a LINHA CRUA normalizada; par
/// <see cref="RegionKind.Changed"/> ⇒ comparação célula a célula: células iguais
/// pós-parse ⇒ região VOLTA a <see cref="RegionKind.Equal"/> (aspas redundantes
/// não são diferença semântica); número de células diferente ou alguma célula
/// divergente ⇒ permanece <see cref="RegionKind.Changed"/> na linha inteira
/// (nunca região sub-linha). Determinismo byte-a-byte herdado do motor; sem
/// campo de tempo.
///
/// Gate herdado (ADR-0011 item 4): placeholder ⇒ <see cref="PlaceholderReadException"/>
/// ANTES de qualquer abertura — zero bytes lidos de placeholder.
/// </summary>
public sealed class CsvComparator : IDocumentComparator
{
    private static readonly char[] DelimitadoresCandidatos = { ',', ';', '\t' };

    /// <inheritdoc cref="IDocumentComparator.Compare"/>
    public ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        GatePlaceholder(left);
        GatePlaceholder(right);

        var linhasLeft = LerLinhasNormalizadas(left.Path);
        var linhasRight = LerLinhasNormalizadas(right.Path);
        char delimLeft = DetectarDelimitador(linhasLeft);
        char delimRight = DetectarDelimitador(linhasRight);

        List<DiffRegion> regioes;
        if (delimLeft != delimRight)
        {
            // Diferença estrutural: nenhuma linha pode casar — todos os documentos
            // inteiros viram UMA região Changed.
            regioes =
                linhasLeft.Count + linhasRight.Count > 0
                    ? [new DiffRegion(RegionKind.Changed, 0, linhasLeft.Count, 0, linhasRight.Count)]
                    : [];
        }
        else
        {
            var cruas = LcsDiff.Diff(linhasLeft, linhasRight);
            regioes = RefinarParesChanged(cruas, linhasLeft, linhasRight, delimLeft);
        }

        bool iguais = regioes.Count == 0
            || regioes.All(r => r.Kind == RegionKind.Equal);
        return new ComparisonResult("csv", iguais, regioes);
    }

    private static void GatePlaceholder(FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }
    }

    /// <summary>
    /// Para cada par Changed cru, compara as células pós-parse 1:1: se TODAS as
    /// linhas pareadas têm as mesmas células, a região volta a Equal (aspas e
    /// delimitadores são sintaxe); caso contrário, segue Changed na linha
    /// inteira. Regiões Added/Removed/Equal passam intactas.
    /// </summary>
    private static List<DiffRegion> RefinarParesChanged(
        List<DiffRegion> regioes,
        List<string> linhasLeft,
        List<string> linhasRight,
        char delimitador)
    {
        var saida = new List<DiffRegion>(regioes.Count);
        foreach (var regiao in regioes)
        {
            if (regiao.Kind != RegionKind.Changed)
            {
                saida.Add(regiao);
                continue;
            }

            bool paresEquivalentes = true;
            int n = Math.Min(regiao.LeftCount, regiao.RightCount);
            for (int k = 0; k < n && paresEquivalentes; k++)
            {
                var celulasL = DividirEmCelulas(linhasLeft[regiao.LeftStart + k], delimitador);
                var celulasR = DividirEmCelulas(linhasRight[regiao.RightStart + k], delimitador);
                if (celulasL.Count != celulasR.Count)
                {
                    paresEquivalentes = false;
                    break;
                }

                for (int c = 0; c < celulasL.Count; c++)
                {
                    if (!string.Equals(celulasL[c], celulasR[c], StringComparison.Ordinal))
                    {
                        paresEquivalentes = false;
                        break;
                    }
                }
            }

            saida.Add(paresEquivalentes && n > 0
                ? new DiffRegion(RegionKind.Equal, regiao.LeftStart, regiao.LeftCount, regiao.RightStart, regiao.RightCount)
                : regiao);
        }

        return saida;
    }

    /// <summary>
    /// Conta ocorrências de cada candidato na primeira linha NÃO-VAZIA e escolhe o
    /// mais frequente; empate resolvido pela ordem fixa ',' ';' '\t'; nenhum
    /// presente ou arquivo sem linha não-vazia ⇒ ','. Linha vazia no início não
    /// derruba a contagem para zero.
    /// </summary>
    private static char DetectarDelimitador(List<string> linhas)
    {
        foreach (var linha in linhas)
        {
            if (linha.Length == 0)
            {
                continue;
            }

            char melhor = DelimitadoresCandidatos[0];
            int melhorContagem = -1;
            foreach (var candidato in DelimitadoresCandidatos)
            {
                int contagem = ContarOcorrenciasForaDeAspas(linha, candidato);
                if (contagem > melhorContagem)
                {
                    melhor = candidato;
                    melhorContagem = contagem;
                }
            }

            return melhor;
        }

        return ',';
    }

    /// <summary>Número de ocorrências de <paramref name="c"/> fora de campos citados.</summary>
    private static int ContarOcorrenciasForaDeAspas(string linha, char c)
    {
        int contagem = 0;
        bool dentro = false;
        for (int i = 0; i < linha.Length; i++)
        {
            char ch = linha[i];
            if (ch == '"')
            {
                if (dentro && i + 1 < linha.Length && linha[i + 1] == '"')
                {
                    i++; // "" é aspa escapada dentro de campo citado
                }
                else
                {
                    dentro = !dentro;
                }
            }
            else if (ch == c && !dentro)
            {
                contagem++;
            }
        }

        return contagem;
    }

    /// <summary>
    /// Divide uma linha crua em células (subconjunto RFC4180): fora de aspas o
    /// delimitador separa campos; dentro de campo citado o delimitador é literal
    /// e "" vira aspa simples. Aspas externas são removidas.
    /// </summary>
    private static List<string> DividirEmCelulas(string linha, char delimitador)
    {
        var celulas = new List<string>();
        var atual = new System.Text.StringBuilder();
        bool dentro = false;
        bool campoCitado = false;
        for (int i = 0; i < linha.Length; i++)
        {
            char ch = linha[i];
            if (ch == '"')
            {
                if (!dentro && atual.Length == 0 && !campoCitado)
                {
                    // abre campo citado no início do campo
                    dentro = true;
                    campoCitado = true;
                }
                else if (dentro && i + 1 < linha.Length && linha[i + 1] == '"')
                {
                    atual.Append('"');
                    i++; // "" → "
                }
                else if (dentro)
                {
                    dentro = false; // fecha campo citado
                }
                else
                {
                    atual.Append('"'); // aspa literal fora de campo citado
                }
            }
            else if (ch == delimitador && !dentro)
            {
                celulas.Add(atual.ToString());
                atual.Clear();
                campoCitado = false;
            }
            else
            {
                atual.Append(ch);
            }
        }

        celulas.Add(atual.ToString());
        return celulas;
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
