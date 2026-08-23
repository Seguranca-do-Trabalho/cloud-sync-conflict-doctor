namespace Doctor.Core;

using System.IO.Enumeration;

/// <summary>
/// Enumeração Level 0 multiplataforma (SPEC §5): varredura recursiva que traz os
/// metadados no próprio diretorio entry — sem chamada stat extra por arquivo.
/// Nunca lê conteúdo. NUNCA atravessa reparse points de diretório (junction,
/// symlink, mount point — regra 4 do ADR-0004, threat-model T-02, PLH-04): o
/// diretório com reparse é FOLHA, registrado em <see cref="EnumerationResult.Errors"/>
/// e a varredura continua. Symlinks de ARQUIVO entram como entrada marcada
/// ReparsePoint (análogo POSIX de reparse point — SPEC §6). A marcação de placeholder
/// é exclusivamente via <see cref="PlaceholderPolicy"/>.
/// A ordem física é a que o filesystem entregar: a ordenação canônica é
/// responsabilidade exclusiva de <see cref="OrderedFileEnumerator"/>.
///
/// Nota de implementação: usa <see cref="FileSystemEnumerator{TResult}"/> direto
/// (e não FileSystemEnumerable+options) porque só o override de ShouldRecurseIntoEntry
/// garante não-descida em reparse: com AttributesToSkip=0 (exigido para os symlinks de
/// ARQUIVO aparecerem marcados), o comportamento padrão do .NET decide a recursão de
/// symlink de diretório seguindo o alvo e entra em ciclos — observado no T09
/// (84 entradas num ciclo a→b→a; conteúdo externo à raiz vazou para a lista).
/// </summary>
public sealed class CrossPlatformEnumerator : IFileEnumerator
{
    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
    {
        var files = new List<FileEntry>();
        var errors = new List<ScanError>();

        using var enumerator = new Enumerador(rootPath, errors);
        while (enumerator.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            if (enumerator.Current is { } item)
            {
                files.Add(item);
            }
        }

        // Folhas reparse registradas durante a descida + falhas de acesso individual:
        // erro nunca aborta o scan (contratos.md R10).
        errors.AddRange(enumerator.ErrosDeAcesso);

        // Telemetria derivada da lista final (nunca de contadores incrementais
        // dependentes de ordem física) — mesmo contrato do OrderedFileEnumerator.
        var telemetry = new ScanTelemetry
        {
            FilesEnumerated = files.Count,
            FilesPlaceholder = files.Count(f => f.IsPlaceholder),
        };

        return new EnumerationResult(files, errors, telemetry);
    }

    /// <summary>
    /// Varredura física: recursão bloqueada em qualquer diretório com bit de reparse
    /// (decisão centralizada nos bits de <see cref="PlaceholderPolicy"/>) — o ciclo
    /// nunca é iniciado, porque a descida é recusada na entrada do diretório.
    /// </summary>
    private sealed class Enumerador : FileSystemEnumerator<FileEntry?>
    {
        private readonly List<ScanError> _erros;

        public Enumerador(string root, List<ScanError> erros)
            : base(root, options: new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0, // nada filtrado: placeholders são MARCADOS, não ocultos
            })
            => _erros = erros;

        public List<ScanError> ErrosDeAcesso { get; } = new();

        /// <summary>
        /// Regra 4 do ADR-0004: diretório com reparse point é SEMPRE folha —
        /// registrado e nunca descido. Vale para junction, symlink de diretório,
        /// mount point, qualquer alvo.
        /// </summary>
        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            // Decisão centralizada: os bits "não tocar" vivem na PlaceholderPolicy.
            if (PlaceholderPolicy.IsPlaceholder(entry.Attributes))
            {
                _erros.Add(new ScanError(
                    entry.ToFullPath(),
                    "reparse point de diretorio (junction/symlink) nao atravessado - folha registrada (ADR-0004 regra 4; threat-model T-02)"));
                return false;
            }

            return true;
        }

        protected override FileEntry? TransformEntry(ref FileSystemEntry entry)
        {
            try
            {
                if (entry.IsDirectory)
                {
                    return null; // somente arquivos na lista Level 0 (reparse de diretório já virou folha registrada)
                }

                // Linux: inode via lstat P/Invoke (LinuxFileId); Windows: preenchido
                // pelo enumerador nativo (WindowsNativeEnumerator), não por este caminho.
                string fileId;
#if WINDOWS
                fileId = "0"; // não usado: build Windows usa WindowsNativeEnumerator
#else
                fileId = LinuxFileId.GetInode(entry.ToFullPath());
#endif

                // Marcação de placeholder NA ORIGEM (SPEC §6, defesa em profundidade):
                // este enumerador é utilizável cru (testes, harness); quem o consumir
                // via OrderedFileEnumerator recebe a policy reaplicada (idempotente).
                var built = new FileEntry
                {
                    Path = entry.ToFullPath(),
                    Size = entry.Length,
                    MtimeUtc = entry.LastWriteTimeUtc,
                    Attributes = entry.Attributes,
                    VolumeId = Environment.MachineName, // identificação local estável; NTFS volume GUID chega com o enumerador nativo
                    FileId = fileId,
                };
                return built with
                {
                    IsPlaceholder = PlaceholderPolicy.IsPlaceholder(built),
                    PlaceholderKind = PlaceholderPolicy.Classify(built),
                };
            }
            catch (Exception ex)
            {
                // Erro individual não aborta o scan — registrado pelo chamador via Errors.
                ErrosDeAcesso.Add(new ScanError(entry.ToFullPath(), ex.Message));
                return null;
            }
        }
    }
}
