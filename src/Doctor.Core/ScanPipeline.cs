namespace Doctor.Core;

/// <summary>
/// Resultado do scan L0→L3 do pipeline (T12 — escopo reduzido do orquestrador):
/// grupos candidatos do Level 1 e os vereditos finais do Level 3 — duplicatas
/// idênticas (hash completo igual em todos os membros do grupo) e conflitos reais
/// (hash completo divergente), classe por classe, sempre em ordem canônica
/// (<see cref="PathOrder"/>; SPEC §3, ADR-0004 regra 1). Não é o relatório final
/// (schema v1 / IReportWriter): é o tipo de decisão serial consumido pelos testes
/// de determinismo deste card.
/// </summary>
public sealed record ScanResult(
    IReadOnlyList<ConflictGroup> Groups,
    IReadOnlyList<IdenticalDuplicate> IdenticalDuplicates,
    IReadOnlyList<RealConflict> RealConflicts,
    IReadOnlyList<UnresolvedGroup>? UnresolvedGroups = null)
{
    /// <summary>Grupos recusados por hash não verificável (S11-5). Nunca nulo.</summary>
    public IReadOnlyList<UnresolvedGroup> UnresolvedGroups { get; init; } =
        UnresolvedGroups ?? [];
}

/// <summary>
/// Grupo cuja decisão L3 foi recusada por impossibilidade de hash completo de ao
/// menos um membro (S11-5 fail-closed, ameaça T-11): sem hash completo verificado,
/// nenhuma classificação é emitida — o grupo não vira duplicata nem conflito e o
/// par nunca é elegível a resolução/quarentena. Motivo auditável por membro.
/// </summary>
public sealed record UnresolvedGroup(
    string NormalizedBaseName,
    long SizeBytes,
    IReadOnlyList<UnresolvedMember> Members);

/// <summary>Membro sem hash completo verificável, com o motivo da recusa.</summary>
public sealed record UnresolvedMember(string Path, string Reason);

/// <summary>
/// Classe de duplicatas idênticas (schema v1 §6.1): hash BLAKE3 completo comum +
/// membros em ordem canônica por caminho. Forma-se apenas de grupo cujos hashes
/// completos são TODOS iguais; grupo nunca gera entrada simultânea aqui e em
/// <see cref="RealConflict"/> (§6.1/§6.2 — classificação por grupo mutuamente exclusiva).
/// </summary>
public sealed record IdenticalDuplicate(string Hash, long SizeBytes, IReadOnlyList<FileEntry> Files);

/// <summary>
/// Conflito real (schema v1 §6.2): grupo candidato com ao menos dois hashes completos
/// distintos após o Level 2. Cobre TODOS os membros do grupo, com o hash completo
/// individual de cada um em ordem canônica por caminho; subconjuntos internamente
/// idênticos ficam reconstruíveis pelos hashes.
/// </summary>
public sealed record RealConflict(
    string NormalizedBaseName,
    long SizeBytes,
    IReadOnlyList<ConflictMember> Files);

/// <summary>Membro de um conflito real: caminho canônico + hash completo individual.</summary>
public sealed record ConflictMember(string Path, string Hash);

/// <summary>
/// Pipeline determinístico L0→L3 (SPEC §5, §11; ADR-0004; docs/contratos.md):
/// enumera (L0), agrupa candidatos por normalized_base_name + size (L1 — Grouping),
/// hashea parcialmente os membros de grupos com 2+ itens (L2 — IHasher.PartialHash)
/// e aplica o hash completo BLAKE3 SOMENTE nas colisões parciais (L3 —
/// Blake3Hasher.FullHashBlake3 via IStreamSource). Toda decisão é serial sobre
/// coleções em ordem canônica; nenhuma decisão depende de ordem de chegada nem de
/// ordem de término de threads (ADR-0004 regras 1-2). Placeholder nunca entra em
/// L1: <see cref="PlaceholderGate"/> remove antes do agrupamento e a política é
/// reclassificada em cada gate de hash (SPEC §6).
/// </summary>
public sealed class ScanPipeline
{
    /// <summary>Sentinela de hash não verificável (S11-5): prefixo + motivo.</summary>
    public const string UnresolvedPrefix = "UNRESOLVED::";

    private readonly IFileEnumerator _enumerator;
    private readonly IHasher _hasher;
    private readonly IStreamSource? _streams;
    private readonly PlaceholderGate _gate;

    /// <param name="enumerator">Fonte L0. Se não for <see cref="OrderedFileEnumerator"/>,
    /// o pipeline reordena pela ordem canônica — a ordenação de saída não depende do
    /// enumerador (SPEC §3).</param>
    /// <param name="hasher">Hasher L2 (parcial). Em produção atravessa
    /// <see cref="PlaceholderGuardedHasher"/>; o pipeline reclassifica por segurança.</param>
    /// <param name="streams">Fonte única de conteúdo do L3 (contratos.md IStreamSource).
    /// Nulo apenas quando <paramref name="hasher"/> é um double de teste que não lê
    /// conteúdo — nesse caso o gate de abertura é o próprio hasher.</param>
    public ScanPipeline(IFileEnumerator enumerator, IHasher hasher, IStreamSource? streams = null)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _streams = streams;
        _gate = new PlaceholderGate(streams ?? new RejectingStreamSource());
    }

    /// <summary>
    /// Executa L0→L3 de forma determinística: mesma árvore ⇒ mesmo
    /// <see cref="ScanResult"/> byte a byte, independentemente da ordem física de
    /// enumeração (SPEC §20; prova automatizada em DET-03 mínimo deste card).
    /// </summary>
    public ScanResult Run(string rootPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // L0 — enumeração + ordem canônica garantida na fronteira do pipeline.
        var enumeration = _enumerator is OrderedFileEnumerator
            ? _enumerator.Enumerate(rootPath, ct)
            : new OrderedFileEnumerator(_enumerator).Enumerate(rootPath, ct);

        // Gate de placeholder (SPEC §6/§21): separa fluxo seguro de Placeholders[].
        var safe = _gate.Enforce(enumeration);

        // L1 — agrupamento de candidatos: serial, entrada já em ordem canônica
        // (Grouping reordena internamente como defesa estrutural — Grouping.Group).
        var groups = Grouping.Group(safe.Files);

        var identical = new List<IdenticalDuplicate>();
        var conflicts = new List<RealConflict>();
        var unresolved = new List<UnresolvedGroup>();

        // L2 + L3 — decisão serial sobre grupos já ordenados (base em bytes, size).
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            var partial = group.Members
                .Select(m => (Member: m, Partial: HashGuarded(() => _hasher.PartialHash(m, ct), m)))
                .ToArray();

            // Colisão parcial = ao menos dois membros com o MESMO hash parcial.
            // Sem colisão parcial não há candidato a duplicata: grupo não sobrevive
            // ao L2 e nenhum full hash acontece (ADR-0004: L3 só sobre sobreviventes).
            // S11-5 fail-closed: hash parcial não verificável (sentinela) força o
            // grupo inteiro para unresolved — sem decisão sobre dado ausente.
            var hasUnresolvedPartial = partial.Any(p =>
                p.Partial.StartsWith(UnresolvedPrefix, StringComparison.Ordinal));

            if (hasUnresolvedPartial)
            {
                unresolved.Add(new UnresolvedGroup(
                    group.NormalizedBaseName,
                    group.SizeBytes,
                    partial
                        .Where(p => p.Partial.StartsWith(UnresolvedPrefix, StringComparison.Ordinal))
                        .Select(p => new UnresolvedMember(p.Member.Path, p.Partial[UnresolvedPrefix.Length..]))
                        .ToArray()));
                continue;
            }

            var hasPartialCollision = partial
                .GroupBy(p => p.Partial, StringComparer.Ordinal)
                .Any(g => g.Count() >= 2);

            if (!hasPartialCollision)
            {
                continue;
            }

            // L3 — hash completo dos sobreviventes do L2, decidido serialmente sobre
            // a lista ordenada (ADR-0004 regra 2). O veredito cobre TODOS os membros
            // do grupo com hash individual (schema v1 §6.2): parcial distinto implica
            // conteúdo distinto, então cada membro participa do registro final.
            var full = new (FileEntry Member, string Full)[partial.Length];
            for (var i = 0; i < partial.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                full[i] = (partial[i].Member, HashGuarded(() => FullHash(partial[i].Member, ct), partial[i].Member));
            }

            ClassifyGroup(group, full, identical, conflicts, unresolved);
        }

        identical.Sort((a, b) => string.CompareOrdinal(a.Files[0].Path, b.Files[0].Path));
        conflicts.Sort((a, b) => string.CompareOrdinal(a.Files[0].Path, b.Files[0].Path));
        unresolved.Sort((a, b) => string.CompareOrdinal(a.Members[0].Path, b.Members[0].Path));

        return new ScanResult(groups, identical.ToArray(), conflicts.ToArray(), unresolved.ToArray());
    }

    /// <summary>
    /// Veredito por grupo, mutuamente exclusivo (schema v1 §6.1/§6.2): hashes
    /// completos todos iguais ⇒ <see cref="IdenticalDuplicate"/>; ao menos dois
    /// distintos ⇒ <see cref="RealConflict"/> cobrindo TODOS os membros.
    /// </summary>
    private void ClassifyGroup(
        ConflictGroup group,
        (FileEntry Member, string Full)[] full,
        List<IdenticalDuplicate> identical,
        List<RealConflict> conflicts,
        List<UnresolvedGroup> unresolved)
    {
        // S11-5 fail-closed (T-11): membro cujo hash completo não pôde ser verificado
        // recusa a decisão do GRUPO INTEIRO — nunca classificar com dado ausente.
        var failed = full
            .Where(f => f.Full.StartsWith(UnresolvedPrefix, StringComparison.Ordinal))
            .Select(f => new UnresolvedMember(f.Member.Path, f.Full[UnresolvedPrefix.Length..]))
            .ToArray();

        if (failed.Length > 0)
        {
            unresolved.Add(new UnresolvedGroup(
                group.NormalizedBaseName,
                group.SizeBytes,
                failed));
            return;
        }

        var distinctFull = full.Select(f => f.Full).Distinct(StringComparer.Ordinal).Count();

        if (distinctFull <= 1)
        {
            identical.Add(new IdenticalDuplicate(
                full[0].Full,
                group.SizeBytes,
                full.Select(f => f.Member).ToArray()));
        }
        else
        {
            conflicts.Add(new RealConflict(
                group.NormalizedBaseName,
                group.SizeBytes,
                full.Select(f => new ConflictMember(f.Member.Path, f.Full)).ToArray()));
        }
    }

    /// <summary>
    /// L3 via fonte única de conteúdo (contratos.md). Com stream source injetado,
    /// <see cref="Blake3Hasher.FullHashBlake3"/> aplica o gate de placeholder antes da
    /// abertura; sem stream source (hasher de teste), o gate já vive no hasher.
    /// </summary>
    private string FullHash(FileEntry entry, CancellationToken ct) =>
        _streams is null
            ? _hasher.FullHash(entry, ct)
            : Blake3Hasher.FullHashBlake3(_gate, entry, ct);

    /// <summary>
    /// Reclassificação defensiva no ponto de decisão (SPEC §6; ameaça T-03): marcação
    /// L0 ausente ou stale nunca vira leitura — <see cref="PlaceholderPolicy"/> tem a
    /// última palavra antes de qualquer hash.
    /// </summary>
    private static string HashGuarded(Func<string> compute, FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }

        try
        {
            return compute();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlaceholderViolationException)
        {
            // S11-5 fail-closed: falha de leitura/hash não aborta o scan e NUNCA
            // produz decisão — o membro carrega o motivo como sentinela auditável
            // consumida por ClassifyGroup (grupo inteiro vira UnresolvedGroup).
            return UnresolvedPrefix + ex.Message;
        }
    }

    /// <summary>
    /// Fonte de conteúdo que recusa toda abertura: usada só para satisfazer
    /// <see cref="PlaceholderGate"/> quando o hasher de teste não lê bytes. Qualquer
    /// abertura aqui é bug de gate, não caminho legítimo.
    /// </summary>
    private sealed class RejectingStreamSource : IStreamSource
    {
        public Stream OpenRead(FileEntry entry) =>
            throw new PlaceholderViolationException(entry.Path, "fonte de conteudo ausente no pipeline:");
    }
}
