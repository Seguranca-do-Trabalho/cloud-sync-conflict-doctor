namespace Doctor.Core;

/// <summary>
/// Level 1 do scanner (SPEC §7; ADR-0004 §5): normalização de nomes de conflito
/// e agrupamento de candidatos por normalized_base_name + size.
///
/// Lista FIXA, PEQUENA e AUDITÁVEL — sem máquina de regex e sem heurística infinita.
/// Sufixo fora da lista permanece intacto: grupos são candidatos; o veredito final é
/// sempre por hash real no Level 2/3 (risco R6 do threat model). Comparação sempre
/// Ordinal (bytes), nunca sensível a locale.
///
/// Versionamento: qualquer mudança na lista ou na semântica de um padrão exige bump
/// de <see cref="NormalizerVersion"/> (= normalization_rules_version do schema v1 §3);
/// relatórios de versões distintas de normalização não são comparáveis em `groups`.
/// </summary>
public static class Grouping
{
    /// <summary>Versão da lista fixa de padrões. Congelada em 1 por este card.</summary>
    public const int NormalizerVersion = 1;

    /// <summary>
    /// Lista fixa e ORDENADA dos padrões da SPEC §7, exatamente como declarada lá.
    /// A ordem é parte da versão: reordenar exige bump de <see cref="NormalizerVersion"/>.
    /// </summary>
    public static readonly string[] ConflictSuffixes =
    [
        " (conflicted copy)",
        "-DESKTOP-XXXX",
        " (1)",
        " (2)",
        "~",
        "~$",
        ".sb-<hex>",
    ];

    /// <summary>Comprimento máximo aceito para o segmento hex de ".sb-&lt;hex&gt;" (limitado e auditável).</summary>
    private const int MaxSbHexLength = 32;

    // Hostname aceito em "-DESKTOP-XXXX": 6 a 12 caracteres alfanuméricos.
    private const int MinHostLength = 6;
    private const int MaxHostLength = 12;

    // Passes limitados até ponto fixo (não laço infinito): cada padrão é aplicado no
    // máximo uma vez por passada e o total de passadas tem limite explícito.
    private const int MaxStripPasses = 8;

    /// <summary>
    /// Nome base após normalização de conflitos, extensão preservada. Recebe NOME DE
    /// ARQUIVO (não caminho). Retorna o nome intacto quando nenhum padrão casa.
    /// </summary>
    public static string NormalizeBaseName(string fileName)
    {
        var baseName = Path.GetFileName(fileName);

        // Passadas limitadas cobrem padrões compostos reais:
        // "~$Relatorio-DESKTOP-ABC123 (conflicted copy).xlsx" → "Relatorio.xlsx".
        for (var pass = 0; pass < MaxStripPasses; pass++)
        {
            var before = baseName;
            foreach (var pattern in ConflictSuffixes)
            {
                baseName = ApplyPattern(baseName, pattern);
            }

            if (baseName == before)
            {
                break;
            }
        }

        return baseName;
    }

    /// <summary>
    /// Agrupa candidatos por (normalized_base_name, size); tamanhos diferentes NUNCA
    /// agrupam (SPEC §7). Devolve APENAS grupos multi-membro (schema v1 §6.0: inventário
    /// completo dos grupos com 2 ou mais membros — grupo unitário não é candidato).
    ///
    /// Determinismo (ADR-0004 §1): nenhuma decisão por ordem de chegada. A entrada é
    /// ordenada pela ordem canônica (<see cref="PathOrder"/>) antes do particionamento,
    /// e os grupos saem por (base em bytes, depois size ascendente), membros por caminho
    /// em bytes — schema v1 §1.3/§6.0.
    /// </summary>
    public static IReadOnlyList<ConflictGroup> Group(IEnumerable<FileEntry> entries)
    {
        var buckets = new Dictionary<(string Base, long Size), List<FileEntry>>();

        // Defesa estrutural: particionamento sobre coleção em ordem canônica,
        // independente da ordem física em que os FileEntry chegarem.
        foreach (var entry in entries.OrderBy(e => e, PathOrder.Comparer))
        {
            var key = (NormalizeBaseName(Path.GetFileName(entry.Path)), entry.Size);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new List<FileEntry>();
                buckets.Add(key, bucket);
            }

            bucket.Add(entry);
        }

        // Grupos ordenados por (base em bytes Ordinal, depois size); membros herdam a
        // ordem canônica da passada única. Só multi-membro vira candidato (§6.0).
        return buckets
            .Where(kv => kv.Value.Count >= 2)
            .Select(kv => new ConflictGroup(kv.Key.Base, kv.Key.Size, kv.Value.ToArray()))
            .OrderBy(g => g.NormalizedBaseName, StringComparer.Ordinal)
            .ThenBy(g => g.SizeBytes)
            .ToArray();
    }

    private static string ApplyPattern(string name, string pattern)
    {
        return pattern switch
        {
            " (conflicted copy)" => StripSuffixBeforeExtension(name, " (conflicted copy)"),
            "-DESKTOP-XXXX" => StripDesktopSuffix(name),
            " (1)" => StripSuffixBeforeExtension(name, " (1)"),
            " (2)" => StripSuffixBeforeExtension(name, " (2)"),
            "~" => StripTildeSuffix(name),
            "~$" => StripOfficeLockPrefix(name),
            ".sb-<hex>" => StripDropboxSuffix(name),
            _ => throw new InvalidOperationException($"Padrão sem caso na lista fixa: {pattern}"),
        };
    }

    /// <summary>Remove sufixo literal que aparece ANTES da extensão ("foto (1).jpg" → "foto.jpg").</summary>
    private static string StripSuffixBeforeExtension(string name, string suffix)
    {
        var dot = name.LastIndexOf('.');
        var stemEnd = dot > 0 ? dot : name.Length; // ".bashrc (1)": ponto inicial não é extensão
        var stem = name[..stemEnd];

        if (!stem.EndsWith(suffix, StringComparison.Ordinal))
        {
            return name;
        }

        var newStemLength = stemEnd - suffix.Length;
        if (newStemLength < 1)
        {
            return name; // conservador: nunca reduz a vazio
        }

        return name[..newStemLength] + (dot > 0 ? name[dot..] : string.Empty);
    }

    /// <summary>"orcamento-DESKTOP-ABC123.xlsx" → "orcamento.xlsx": marcador "-DESKTOP-" + hostname alfanumérico de 6–12 caracteres junto à extensão, Ordinal, sem regex.</summary>
    private static string StripDesktopSuffix(string name)
    {
        const string marker = "-DESKTOP-";
        var dot = name.LastIndexOf('.');
        var stemEnd = dot > 0 ? dot : name.Length;

        var markerIdx = name.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIdx < 1 || markerIdx + marker.Length >= stemEnd)
        {
            return name;
        }

        var hostStart = markerIdx + marker.Length;
        var hostLen = stemEnd - hostStart;
        if (hostLen is < MinHostLength or > MaxHostLength)
        {
            return name;
        }

        for (var i = hostStart; i < stemEnd; i++)
        {
            if (!IsAlphanumeric(name[i]))
            {
                return name;
            }
        }

        return name[..markerIdx] + (dot > 0 ? name[dot..] : string.Empty);
    }

    private static bool IsAlphanumeric(char c) =>
        c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    /// <summary>"backup.txt~" → "backup.txt"; nunca reduz a vazio.</summary>
    private static string StripTildeSuffix(string name) =>
        name.EndsWith("~", StringComparison.Ordinal) && name.Length >= 2
            ? name[..^1]
            : name;

    /// <summary>"~$curriculo.docx" → "curriculo.docx" (lockfile Office). Prefixo, não sufixo; nunca reduz a vazio.</summary>
    private static string StripOfficeLockPrefix(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) && name.Length > 2
            ? name[2..]
            : name;

    /// <summary>
    /// "backup.sb-a3f19c.txt" → "backup.txt": marcador ".sb-" seguido de 1–32 hex
    /// minúsculos imediatamente antes da extensão. Segmento não-hex não casa:
    /// sufixo desconhecido permanece intacto (GRP-02, limitado e auditável).
    /// </summary>
    private static string StripDropboxSuffix(string name)
    {
        var dot = name.LastIndexOf('.');
        var stemEnd = dot > 0 ? dot : name.Length;

        var markerIdx = name.LastIndexOf(".sb-", StringComparison.Ordinal);
        if (markerIdx < 1 || markerIdx + 4 >= stemEnd)
        {
            return name;
        }

        var segLen = stemEnd - markerIdx - 4;
        if (segLen > MaxSbHexLength)
        {
            return name;
        }

        for (var i = markerIdx + 4; i < stemEnd; i++)
        {
            var c = name[i];
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return name; // ".sb-ZZ": fora do domínio hex → intocado
            }
        }

        return name[..markerIdx] + (dot > 0 ? name[dot..] : string.Empty);
    }
}

/// <summary>
/// Grupo candidato do Level 1 (schema v1 §6.0): base normalizada + size compartilhado
/// pelos membros, sempre ≥ 2. Membros em ordem canônica por caminho (bytes UTF-8).
/// </summary>
public sealed record ConflictGroup(
    string NormalizedBaseName,
    long SizeBytes,
    IReadOnlyList<FileEntry> Members);
