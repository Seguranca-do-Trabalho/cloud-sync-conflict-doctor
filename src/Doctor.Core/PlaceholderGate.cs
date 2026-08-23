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
/// Violação do GATE DE PIPELINE (SPEC §6/§21 — escopo T09 reduzido pelo orquestrador):
/// lançada quando algum código tenta obter conteúdo de placeholder fora do fluxo
/// permitido, ou quando a telemetria recebida já carrega PlaceholderBytesRead != 0
/// (valor != 0 é violação de segurança, não dado — Telemetry.cs). Nunca é capturada
/// para "seguir em frente": quem a recebe tem um bug de gate a corrigir.
/// </summary>
[DebuggerDisplay("PlaceholderViolationException: {" + nameof(EntryPath) + "}")]
public sealed class PlaceholderViolationException : InvalidOperationException
{
    public PlaceholderViolationException(string? entryPath, string motivo)
        : base($"Violacao do gate de placeholder (SPEC §6): {motivo} {entryPath ?? "(sem caminho)"}")
        => EntryPath = entryPath;

    /// <summary>Caminho da entrada rejeitada; null quando a violação é de telemetria.</summary>
    public string? EntryPath { get; }
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

/// <summary>
/// GATE DE PIPELINE (T09 — SPEC §6/§21; ADR-0005 item 6): ponto único por onde TODO
/// acesso a conteúdo passa após a enumeração Level 0. Garante POR CONSTRUÇÃO que
/// placeholder não gera leitura:
/// 1. <see cref="Enforce"/> separa o resultado L0 em fluxo seguro (não-placeholders,
///    para L1/L2/L3) e lista Placeholders[] do relatório (schema v1 §6.3);
/// 2. <see cref="OpenRead"/> reclassifica cada abertura via <see cref="PlaceholderPolicy"/>
///    (duplo gate: marcação L0 + bits crus) e lança <see cref="PlaceholderViolationException"/>
///    ANTES de tocar a fonte;
/// 3. recusa telemetria de entrada já violada — o gate nunca "lava" um contador
///    PlaceholderBytesRead != 0.
/// </summary>
public sealed class PlaceholderGate
{
    private readonly IStreamSource _streams;

    public PlaceholderGate(IStreamSource streams)
        => _streams = streams ?? throw new ArgumentNullException(nameof(streams));

    /// <summary>
    /// Aplica o gate ao resultado da enumeração: devolve relatório parcial com apenas
    /// entradas seguras em <c>Files</c>, placeholders projetados em
    /// <see cref="PartialReport.Placeholders"/> e telemetria derivada com
    /// <c>placeholder_bytes_read == 0</c> garantido por construção. Lança
    /// <see cref="PlaceholderViolationException"/> se a entrada já vier violada.
    /// </summary>
    public PartialReport Enforce(EnumerationResult enumeration)
    {
        ArgumentNullException.ThrowIfNull(enumeration);

        if (enumeration.Telemetry.PlaceholderBytesRead != 0)
            throw new PlaceholderViolationException(null, "telemetria de entrada ja violada (placeholder_bytes_read != 0):");

        var files = new List<FileEntry>();
        var placeholders = new List<PlaceholderRecord>();

        foreach (var entry in enumeration.Files)
        {
            // Duplo gate: flag do Level 0 OU qualquer bit suspeito nos atributos crus.
            if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
                placeholders.Add(PlaceholderReport.Records(new[] { entry }).Single());
            else
                files.Add(entry);
        }

        return new PartialReport(
            files.ToArray(),
            placeholders.OrderBy(p => p.Path, StringComparer.Ordinal).ToArray(),
            enumeration.Telemetry with
            {
                FilesEnumerated = enumeration.Files.Count,
                FilesPlaceholder = placeholders.Count,
                PlaceholderBytesRead = 0,
            });
    }

    /// <summary>Único caminho legítimo para conteúdo pós-L0. Recusa placeholder.</summary>
    public Stream OpenRead(FileEntry entry)
    {
        if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
            throw new PlaceholderViolationException(entry.Path, "abertura de conteudo bloqueada:");

        return _streams.OpenRead(entry);
    }
}

/// <summary>Relatório parcial produzido pelo gate: base de L1/L2/L3 e do relatório final.</summary>
public sealed record PartialReport(
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<PlaceholderRecord> Placeholders,
    ScanTelemetry Telemetry);
