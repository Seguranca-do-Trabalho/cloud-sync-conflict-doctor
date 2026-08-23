namespace Doctor.Core;

/// <summary>
/// Comparador textual linha a linha (SPEC §16 "Texto"; ADR-0011 item 2; contratos.md
/// IDocumentComparator). Normalização antes do diff: CRLF→LF; BOM (UTF-8/UTF-16)
/// reconhecido e ignorado — BOM divergente SOZINHO não torna os arquivos diferentes.
/// Comparação de linha exata pos-normalização, sem trim. Motor:
/// <see cref="LcsDiff"/> (ADR-0011 item 5 — LCS próprio, sem dependência externa).
/// Gate herdado (ADR-0011 item 4): placeholder ⇒ <see cref="PlaceholderReadException"/>
/// ANTES de qualquer abertura — zero bytes lidos de placeholder.
/// </summary>
public sealed class TextComparator : IDocumentComparator
{
    /// <summary>Extensões tratadas como texto puro no v1 (decisão do orquestrador, card T23).</summary>
    public static readonly IReadOnlyList<string> ExtensoesTexto = new[]
    {
        ".txt", ".log", ".ini", ".cfg", ".conf",
    };

    /// <inheritdoc cref="IDocumentComparator.Compare"/>
    public ComparisonResult Compare(FileEntry left, FileEntry right, CancellationToken ct)
    {
        GatePlaceholder(left);
        GatePlaceholder(right);

        var linhasLeft = LerLinhasNormalizadas(left.Path);
        var linhasRight = LerLinhasNormalizadas(right.Path);

        var regioes = LcsDiff.Diff(linhasLeft, linhasRight);
        bool iguais = regioes.Count == 0
            || regioes.All(r => r.Kind == RegionKind.Equal);
        return new ComparisonResult("text", iguais, regioes);
    }

    private static void GatePlaceholder(FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }
    }

    /// <summary>Lê o arquivo como UTF-8, remove BOM se presente e divide em linhas com fim LF.</summary>
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
