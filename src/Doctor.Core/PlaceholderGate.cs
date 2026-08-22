namespace Doctor.Core;

using System.Diagnostics;

/// <summary>
/// Exceção de segurança (SPEC §6 "NÃO TOCAR"; ADR-0005 item 6; threat-model T-03):
/// sinaliza tentativa de ler/hashear conteúdo de placeholder. Nunca é capturada para
/// "seguir em frente" — quem a recebe tem um bug de gate a corrigir.
/// </summary>
[DebuggerDisplay("PlaceholderReadException: {" + nameof(EntryPath) + "}")]
public sealed class PlaceholderReadException : InvalidOperationException
{
    public PlaceholderReadException(string entryPath)
        : base($"Placeholder NAO PODE ser aberto nem hasheado (SPEC §6): {entryPath}")
        => EntryPath = entryPath;

    /// <summary>Caminho do placeholder rejeitado — diagnóstico e telemetria de teste.</summary>
    public string EntryPath { get; }
}

/// <summary>
/// GATE DURO (SPEC §6/§21; ADR-0004 regra 3; ADR-0005 item 6; threat-model T-03):
/// TODO IHasher do produto atravessa este decorator. Antes de qualquer abertura de
/// stream, a entrada é reclassificada por <see cref="PlaceholderPolicy"/> — o único
/// ponto de decisão do produto. Bloqueia:
/// 1. entradas marcadas IsPlaceholder no Level 0;
/// 2. entradas não marcadas cujos atributos crus revelem placeholder (duplo gate da
///    mitigação R3/T-03: marcação ausente ou stale não passa).
/// Consequência automatizada: placeholder_bytes_read == 0 (PLH-01/02).
/// </summary>
public sealed class PlaceholderGuardedHasher : IHasher
{
    private readonly IHasher _inner;

    public PlaceholderGuardedHasher(IHasher inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string PartialHash(FileEntry entry, CancellationToken ct = default)
    {
        Enforce(entry);
        return _inner.PartialHash(entry, ct);
    }

    public string FullHash(FileEntry entry, CancellationToken ct = default)
    {
        Enforce(entry);
        return _inner.FullHash(entry, ct);
    }

    private static void Enforce(FileEntry entry)
    {
        // Duplo gate: flag do Level 0 OU qualquer bit suspeito nos atributos crus.
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
        {
            throw new PlaceholderReadException(entry.Path);
        }
    }
}
