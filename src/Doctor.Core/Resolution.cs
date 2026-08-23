namespace Doctor.Core;

/// <summary>
/// T14 — Motor de resolução (SPEC §17; ADR-0003 regra 2). Produz APENAS O PLANO
/// (<see cref="ResolutionPlan"/>) para cada grupo do ScanPipeline: vencedor e lista
/// de sacrificados, ambos determinísticos. Nenhuma operação de arquivo acontece aqui
/// (movimentação/quarentena é fase downstream — SPEC §18, ADR-0002).
///
/// Desempate obrigatório em QUALQUER decisão entre entradas distintas:
/// mtime (mais recente vence) → size (maior vence) → path crescente byte-wise
/// (<see cref="string.CompareOrdinal"/>). Nunca first-seen, nunca ordem de chegada,
/// nunca ordem do filesystem (SPEC §17 "Empates"; ADR-0003).
///
/// Falha fechado (SPEC §2.1): entrada inválida (grupo vazio/nulo, escolha manual
/// fora do grupo, machineId sem representante) lança — não devolve plano plausível.
/// </summary>
public static class Resolution
{
    /// <summary>
    /// Plano de resolução de um grupo: <paramref name="winner"/> fica no lugar,
    /// <paramref name="sacrifices"/> seguem para quarentena downstream (fase futura,
    /// fora deste card). Sacrificados sempre em ordem canônica por caminho byte-wise.
    /// </summary>
    public sealed record ResolutionPlan(
        ConflictGroup Group,
        ResolutionStrategy Strategy,
        FileEntry Winner,
        IReadOnlyList<Sacrifice> Sacrifices);

    /// <summary>Membro destinado à quarentena pelo plano, com o motivo auditável.</summary>
    public sealed record Sacrifice(FileEntry Entry, string Reason);

    /// <summary>
    /// Estratégia keep-newest (SPEC §17 "manter mais recente"): vence o mtime UTC mais
    /// recente; empate absoluto de mtime cai no desempate size → path.
    /// </summary>
    public static ResolutionPlan Resolve(ConflictGroup group, KeepNewest strategy)
    {
        var ordered = OrderByTieBreak(group.Members, m => m.MtimeUtc);
        return BuildPlan(group, strategy, ordered);
    }

    /// <summary>
    /// Estratégia keep-largest (SPEC §17 "manter maior"): vence o size maior; empate
    /// absoluto de size cai no desempate obrigatório mtime → path.
    /// </summary>
    public static ResolutionPlan Resolve(ConflictGroup group, KeepLargest strategy)
    {
        var ordered = OrderByTieBreak(group.Members, m => m.Size);
        return BuildPlan(group, strategy, ordered);
    }

    /// <summary>
    /// Estratégia keep-machine (SPEC §17 "manter versão de determinada máquina"):
    /// vence o membro cujo nome carrega o marcador de conflito "-DESKTOP-&lt;machineId&gt;"
    /// (SPEC §7), Ordinal. Sem representante no grupo: lança (falha fechada) — nunca
    /// devolve plano plausível para pedido não atendível.
    /// </summary>
    public static ResolutionPlan Resolve(ConflictGroup group, KeepMachine strategy)
    {
        var marker = "-DESKTOP-" + strategy.MachineId;
        var representative = group.Members
            .Where(m => Path.GetFileName(m.Path).Contains(marker, StringComparison.Ordinal))
            .ToArray();

        if (representative.Length == 0)
        {
            throw new InvalidOperationException(
                $"keep-machine: nenhum membro da máquina '{strategy.MachineId}' no grupo.");
        }

        // Entre representantes da MESMA máquina (caso patológico), desempate padrão.
        var ordered = OrderByTieBreak(representative, m => m.MtimeUtc);

        // Sacrificados: TODOS os não-vencedores do grupo, em ordem canônica por caminho.
        return BuildPlan(group, strategy, ordered, exclude: representative[0]);
    }

    /// <summary>
    /// Ordenação canônica de CANDIDATOS a vencedor: chave de estratégia desc (o melhor
    /// primeiro), depois desempate obrigatório mtime desc → size desc → path asc.
    /// Primeiro elemento é o vencedor; os demais viram sacrificados em ordem canônica.
    /// </summary>
    private static IOrderedEnumerable<FileEntry> OrderByTieBreak<Tkey>(
        IEnumerable<FileEntry> members,
        Func<FileEntry, Tkey> key) where Tkey : IComparable<Tkey>
    {
        return members.OrderByDescending(key)
            .ThenByDescending(m => m.MtimeUtc)
            .ThenByDescending(m => m.Size)
            .ThenBy(m => m.Path, StringComparer.Ordinal);
    }

    private static ResolutionPlan BuildPlan(
        ConflictGroup group,
        ResolutionStrategy strategy,
        IOrderedEnumerable<FileEntry> ordered,
        FileEntry? exclude = null)
    {
        var list = ordered.ToArray();
        if (exclude is null && (list.Length == 0 || list.Length != group.Members.Count))
        {
            throw new InvalidOperationException("Grupo sem membros ou corrompido: falha fechada.");
        }

        var winner = exclude ?? list[0];
        var sacrifices = group.Members
            .Where(m => !ReferenceEquals(m, winner))
            .OrderBy(m => m.Path, StringComparer.Ordinal)
            .Select(m => new Sacrifice(m, ReasonFor(strategy)))
            .ToArray();

        return new ResolutionPlan(group, strategy, winner, sacrifices);
    }

    private static string ReasonFor(ResolutionStrategy strategy) => strategy switch
    {
        KeepNewest => "keep-newest",
        KeepLargest => "keep-largest",
        KeepMachine k => $"keep-machine:{k.MachineId}",
        KeepManual m => "keep-manual",
        _ => throw new InvalidOperationException("Estratégia desconhecida: falha fechada."),
    };
}

/// <summary>Estratégia abstrata do plano (auditável no manifesto futuro).</summary>
public abstract record ResolutionStrategy;

/// <summary>SPEC §17 "manter mais recente".</summary>
public sealed record KeepNewest : ResolutionStrategy;

/// <summary>SPEC §17 "manter maior".</summary>
public sealed record KeepLargest : ResolutionStrategy;

/// <summary>SPEC §17 "manter versão de determinada máquina" — máquina identificada pelo marcador de conflito "-DESKTOP-&lt;host&gt;" (SPEC §7) embutido no nome do arquivo.</summary>
public sealed record KeepMachine(string MachineId) : ResolutionStrategy;

/// <summary>SPEC §17 "escolher manualmente" — escolha explícita do usuário.</summary>
public sealed record KeepManual(string ChoicePath) : ResolutionStrategy;
