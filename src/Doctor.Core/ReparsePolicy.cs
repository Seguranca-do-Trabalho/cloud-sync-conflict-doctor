namespace Doctor.Core;

/// <summary>
/// Política de reparse do enumerador ordenado (SPEC §6; threat-model T-02 — mitigação
/// obrigatória em profundidade). PURA sobre metadados: decide marcação, rejeição de
/// caminhos que voltam ao mesmo inode e teto de profundidade — sem I/O.
/// </summary>
public sealed record ReparsePolicy
{
    /// <summary>
    /// Teto de profundidade da enumeração (threat-model T-02): entradas mais fundas que
    /// isto são rejeitadas com registro em <see cref="EnumerationResult.Errors"/> —
    /// nunca silenciosamente (contratos.md R10).
    /// </summary>
    public const int DefaultMaxDepth = 16;

    public int MaxDepth { get; init; } = DefaultMaxDepth;
}
