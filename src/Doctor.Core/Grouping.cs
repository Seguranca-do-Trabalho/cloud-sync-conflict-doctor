namespace Doctor.Core;

/// <summary>
/// Level 1 of the scanner (SPEC §7; ADR-0004 §5): conflict name normalization
/// and candidate grouping by normalized_base_name + size.
///
/// FIXED, SMALL and AUDITABLE list — no regex engine and no infinite heuristic.
/// Suffix outside the list remains intact: groups are candidates; the final verdict is
/// always by real hash at Level 2/3 (threat model risk R6). Comparison always
/// Ordinal (bytes), never locale-sensitive.
///
/// Versioning: any change in the list or in the semantics of a pattern requires bumping
/// <see cref="NormalizerVersion"/> (= normalization_rules_version of schema v1 §3);
/// reports from distinct normalization versions are not comparable in `groups`.
/// </summary>
public static class Grouping
{
    /// <summary>Fixed pattern list version. Frozen at 1 by this card.</summary>
    public const int NormalizerVersion = 1;

    /// <summary>
    /// Fixed and ORDERED list of SPEC §7 patterns, exactly as declared there.
    /// The order is part of the version: reordering requires bumping <see cref="NormalizerVersion"/>.
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

    /// <summary>Maximum accepted length for the hex segment of ".sb-&lt;hex&gt;" (limited and auditable).</summary>
    private const int MaxSbHexLength = 32;

    // Accepted hostname in "-DESKTOP-XXXX": 6 to 12 alphanumeric characters.
    private const int MinHostLength = 6;
    private const int MaxHostLength = 12;

    // Limited passes to fixed point (not infinite loop): each pattern is applied at
    // most once per pass and the total pass count has an explicit limit.
    private const int MaxStripPasses = 8;

    /// <summary>
    /// Base name after conflict normalization, extension preserved. Receives a
    /// FILE NAME (not path). Returns the name intact when no pattern matches.
    /// </summary>
    public static string NormalizeBaseName(string fileName)
    {
        var baseName = Path.GetFileName(fileName);

        // Limited passes cover real compound patterns:
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
    /// Groups candidates by (normalized_base_name, size); different sizes NEVER
    /// group (SPEC §7). Returns ONLY multi-member groups (schema v1 §6.0: complete
    /// inventory of groups with 2 or more members — single-member group is not a candidate).
    ///
    /// Determinism (ADR-0004 §1): no decision by arrival order. The input is
    /// sorted by canonical order (<see cref="PathOrder"/>) before partitioning,
    /// and groups exit by (base in bytes, then size ascending), members by path
    /// in bytes — schema v1 §1.3/§6.0.
    /// </summary>
    public static IReadOnlyList<ConflictGroup> Group(IEnumerable<FileEntry> entries)
    {
        var buckets = new Dictionary<(string Base, long Size), List<FileEntry>>();

        // Structural defense: partitioning over collection in canonical order,
        // independent of the physical order FileEntry arrived.
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

        // Groups sorted by (base in Ordinal bytes, then size); members inherit the
        // canonical order from the single pass. Only multi-member becomes candidate (§6.0).
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
            _ => throw new InvalidOperationException($"Pattern without case in fixed list: {pattern}"),
        };
    }

    /// <summary>Removes literal suffix appearing BEFORE the extension ("foto (1).jpg" → "foto.jpg").</summary>
    private static string StripSuffixBeforeExtension(string name, string suffix)
    {
        var dot = name.LastIndexOf('.');
        var stemEnd = dot > 0 ? dot : name.Length; // ".bashrc (1)": leading dot is not extension
        var stem = name[..stemEnd];

        if (!stem.EndsWith(suffix, StringComparison.Ordinal))
        {
            return name;
        }

        var newStemLength = stemEnd - suffix.Length;
        if (newStemLength < 1)
        {
            return name; // conservative: never reduce to empty
        }

        return name[..newStemLength] + (dot > 0 ? name[dot..] : string.Empty);
    }

    /// <summary>"orcamento-DESKTOP-ABC123.xlsx" → "orcamento.xlsx": "-DESKTOP-" marker + 6–12 char alphanumeric hostname next to extension, Ordinal, no regex.</summary>
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

    /// <summary>"backup.txt~" → "backup.txt"; never reduces to empty.</summary>
    private static string StripTildeSuffix(string name) =>
        name.EndsWith("~", StringComparison.Ordinal) && name.Length >= 2
            ? name[..^1]
            : name;

    /// <summary>"~$curriculo.docx" → "curriculo.docx" (Office lockfile). Prefix, not suffix; never reduces to empty.</summary>
    private static string StripOfficeLockPrefix(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) && name.Length > 2
            ? name[2..]
            : name;

    /// <summary>
    /// "backup.sb-a3f19c.txt" → "backup.txt": ".sb-" marker followed by 1–32 lowercase
    /// hex immediately before the extension. Non-hex segment does not match:
    /// unknown suffix remains intact (GRP-02, limited and auditable).
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
                return name; // ".sb-ZZ": outside hex domain → untouched
            }
        }

        return name[..markerIdx] + (dot > 0 ? name[dot..] : string.Empty);
    }
}

/// <summary>
/// Level 1 candidate group (schema v1 §6.0): normalized base + shared size
/// among members, always ≥ 2. Members in canonical path order (UTF-8 bytes).
/// </summary>
public sealed record ConflictGroup(
    string NormalizedBaseName,
    long SizeBytes,
    IReadOnlyList<FileEntry> Members);
