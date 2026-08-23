namespace Doctor.Core;

using System.Text;

/// <summary>
/// Módulo ÚNICO de canonização e contenção de caminhos ABSOLUTOS na fronteira do
/// filesystem (card S11-1/t_17f56008; threat-model T-01 mitigações (a)/(b)/(c);
/// regras R2/R12; SPEC §7–§9; ADR-0002 falha fechada; decisões D4/D5/D6 do card).
/// Complementar a <see cref="CanonicalPath"/> (gate de NOMES relativos do T17):
/// aquele aprova nomes antes de existirem no volume; este governa caminhos que o
/// sistema operacional já materializou ou vai materializar.
///
/// Contrato:
/// (a) <see cref="CanonicalizeRoot"/> canoniza a raiz UMA única vez na entrada —
///     resolução plena via GetFullPath e conversão para a forma estendida \\?\ —
///     e é ESTA string, byte-exata, que alimenta toda comparação de contenção.
///     <see cref="ToExtendedLength"/> desliga em Windows a resolução Win32 que trunca
///     caminhos acima de 260 chars e descarta trailing dot/space: os dois vetores
///     do T-01. Em POSIX é identidade declarada (não existe limite nem reescrita).
/// (b) <see cref="Combine"/> combina segmentos ESTRUTURALMENTE com re-canonicalização
///     a cada passo — concatenação crua de strings é proibida no produto. Um segmento
///     absoluto injetado não é sanitizado em silêncio: a re-canonicalização torna o
///     desvio EXPLÍCITO no produto e a contenção o reprova (falha fechada — nunca
///     "conserto").
/// (c) <see cref="EnsureContained"/> verifica contenção BYTE-A-BYTE (D4:
///     StringComparison.Ordinal sobre UTF-16 code units — nenhum fold Unicode/locale
///     numa decisão de caminho, coerente com o StringComparer.Ordinal de
///     <see cref="PathOrder"/>) do caminho resolvido contra o prefixo canônico da raiz,
///     ANTES de toda operação de escrita/move. Fora da raiz ⇒
///     <see cref="PathEscapeException"/> — falha fechada, operação abortada sem tocar nada.
///
/// Nomes são preservados EXATAMENTE como o filesystem os dá (T-01 mitigação (c)):
/// nada aqui corrige trailing dot/space, reservados ou caixa. A detecção de caracteres
/// de controle bidi para o RELATÓRIO é <see cref="HasBidiControlChars"/> — marcador
/// estrutural (D5); a renderização/escape cabe ao EPIC 10 (R12). O agrupamento por
/// normalized_base_name permanece em bytes UTF-8 exatos (SPEC §7): homóglifo é OUTRO
/// nome e nunca é fundido aqui.
///
/// Consumidores obrigatórios (D6): quarentena move/restore hoje; os EPICs 03/07/08
/// consomem esta mesma primitiva com seus testes de integração próprios.
/// </summary>
public static class PathCanonical
{
    /// <summary>Prefixo estendido de dispositivo Win32.</summary>
    private const string PrefixoEstendido = @"\??\";

    /// <summary>Prefixo estendido UNC.</summary>
    private const string PrefixoUncEstendido = @"\??\UNC\";

    /// <summary>
    /// Caracteres de controle Unicode bidirecional (R12/D5): embutidos num nome,
    /// reordenam a RENDERIZAÇÃO ("fdp\u202Eexe.pdf" exibe "exe.pdf") sem alterar os
    /// bytes armazenados. Lista fixa e auditável — U+202A–U+202E (embedding/overrides),
    /// U+2066–U+2069 (isolates), U+200E/U+200F (marcas LRM/RLM) e U+061C (árabe).
    /// </summary>
    private static readonly ReadOnlyMemory<char> BidiControls = new[]
    {
        '\u202A', '\u202B', '\u202C', '\u202D', '\u202E',
        '\u2066', '\u2067', '\u2068', '\u2069',
        '\u200E', '\u200F',
        '\u061C',
    };

    /// <summary>
    /// Forma canônica da RAIZ: absoluta, resolvida e convertida para a forma estendida.
    /// Canonize UMA vez na entrada; reutilize o retorno byte-exato em toda contenção.
    /// </summary>
    public static string CanonicalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return ToExtendedLength(Path.GetFullPath(root));
    }

    /// <summary>
    /// Conversão para a forma estendida \\?\ (idempotente). Em Windows desliga a
    /// resolução Win32 que trunca acima de 260 chars e descarta trailing dot/space —
    /// os dois vetores do T-01. Em POSIX é identidade declarada e documentada: não
    /// existe o limite nem a reescrita. Caminho já estendido volta intocado.
    /// </summary>
    public static string ToExtendedLength(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsWindows())
        {
            return path; // POSIX: sem limite MAX_PATH; identidade explícita e documentada.
        }

        if (path.StartsWith(PrefixoEstendido, StringComparison.Ordinal))
        {
            return path; // idempotente
        }

        // UNC ("\servidor\share\x") usa o dispositivo UNC do namespace estendido.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PrefixoUncEstendido + path[2..];
        }

        // Caminho drive-relativo ("C:x.txt") precisa do completo antes do prefixo.
        var completo = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

        return PrefixoEstendido + completo;
    }

    /// <summary>
    /// Combinação ESTRUTURAL de caminhos com RE-CANONIZAÇÃO a cada segmento (R2:
    /// concatenação crua proibida). Cada segmento é resolvido contra a base acumulada;
    /// um segmento ROOTED (absoluto) substitui a base na re-canonicalização — o desvio
    /// fica EXPLÍCITO no produto em vez de mascarado, e cabe ao chamador fechá-lo com
    /// <see cref="EnsureContained"/>, exigida em todo ponto de escrita/move deste produto.
    /// A saída sai sempre na forma estendida canônica.
    /// </summary>
    public static string Combine(string root, params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var acumulado = Path.GetFullPath(root);

        foreach (var segmento in segments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segmento);

            // Re-canonicalização estrutural: rooted reinicia a resolução (desvio
            // visível), relativo desce da base (GetFullPath resolve "."/"..",
            // separadores duplicados e componentes redundantes).
            acumulado = Path.IsPathRooted(segmento)
                ? Path.GetFullPath(segmento)
                : Path.GetFullPath(segmento, acumulado);
        }

        return ToExtendedLength(acumulado);
    }

    /// <summary>
    /// Verificação de CONTENÇÃO byte-a-byte (D4) do caminho resolvido contra a raiz
    /// canônica. Chamar ANTES de toda operação de escrita/move. Comparação Ordinal
    /// (UTF-16 code units): "/raiz-evil" vs "/raiz" é REPROVADO pelo exame do caractere
    /// de fronteira. Fora da raiz ⇒ <see cref="PathEscapeException"/>.
    /// </summary>
    public static void EnsureContained(string path, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var resolvido = ToExtendedLength(Path.GetFullPath(path));
        var raizCanonica = CanonicalizeRoot(root);

        if (!resolvido.StartsWith(raizCanonica, StringComparison.Ordinal))
        {
            throw new PathEscapeException(path, root, resolvido);
        }

        // Dentro do prefixo: ou É a raiz, ou o próximo caractere TEM que ser separador.
        if (resolvido.Length > raizCanonica.Length)
        {
            var fronteira = resolvido[raizCanonica.Length];
            if (fronteira != Path.DirectorySeparatorChar
                && fronteira != Path.AltDirectorySeparatorChar
                && fronteira != Path.VolumeSeparatorChar)
            {
                throw new PathEscapeException(path, root, resolvido);
            }
        }
    }

    /// <summary>
    /// Verdadeiro se o NOME contém caractere de controle bidi (lista fixa acima).
    /// Função PURA sobre UTF-16 code units: nenhum fold visual/locale — homóglifo NÃO
    /// é detectado aqui (homóglifo é outro nome legítimo; agrupamento fica por bytes
    /// exatos no Level 1, SPEC §7). Alimenta o marcador estrutural
    /// <see cref="FileEntry.HasBidiControlChars"/> (D5) e o escape do relatório (R12).
    /// </summary>
    public static bool HasBidiControlChars(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var tabela = BidiControls.Span;

        foreach (var c in name)
        {
            foreach (var proibido in tabela)
            {
                if (c == proibido)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// Falha FECHADA de contenção (ADR-0002 item 4; T-01/R2): um caminho resolvido escapou
/// do prefixo canônico da raiz. Carrega pretendido, raiz e resolvido para auditoria.
/// Nenhuma operação destrutiva prossegue após esta exceção (R11).
/// </summary>
public sealed class PathEscapeException : InvalidOperationException
{
    public PathEscapeException(string caminhoPretendido, string raiz, string caminhoResolvido)
        : base($"Contenção violada (T-01/R2): '{caminhoResolvido}' está fora da raiz canônica '{raiz}' (pretendido: '{caminhoPretendido}'). Operação recusada.")
    {
        RequestedPath = caminhoPretendido;
        Root = raiz;
        ResolvedPath = caminhoResolvido;
    }

    /// <summary>Caminho pretendido pelo chamador (antes da canonização).</summary>
    public string RequestedPath { get; }

    /// <summary>Raiz canônica exigida como prefixo byte-a-byte.</summary>
    public string Root { get; }

    /// <summary>Caminho após re-canonicalização (forma estendida).</summary>
    public string ResolvedPath { get; }
}
