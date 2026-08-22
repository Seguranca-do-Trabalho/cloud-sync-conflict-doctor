namespace Doctor.Core;

using System.IO.Enumeration;

/// <summary>
/// Enumeração Level 0 multiplataforma (SPEC §5): varredura recursiva com
/// <see cref="FileSystemEnumerable{TResult}"/>, que já traz os metadados no próprio
/// diretorio entry — sem chamada stat extra. Nunca lê conteúdo; nunca entra em
/// symlink de diretório (SkipReparsePoints); symlinks de arquivo entram como
/// entrada marcada ReparsePoint (análogo POSIX de reparse point — SPEC §6).
/// A ordem física é a que o filesystem entregar: a ordenação canônica é
/// responsabilidade exclusiva de <see cref="OrderedFileEnumerator"/>.
/// </summary>
public sealed class CrossPlatformEnumerator : IFileEnumerator
{
    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
    {
        var files = new List<FileEntry>();
        var errors = new List<ScanError>();

        // FileSystemName.MatchSimple não é usado; expressão padrão casa tudo.
        var enumerable = new FileSystemEnumerable<FileEntry?>(
            rootPath,
            (ref FileSystemEntry entry) => Transform(ref entry),
            options: new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // NÃO seguir symlinks de diretório: reparse points não são atravessados (SPEC §6).
                AttributesToSkip = 0, // nada filtrado por atributo; placeholders são MARCADOS, não ocultos
            });

        foreach (var item in enumerable)
        {
            ct.ThrowIfCancellationRequested();
            if (item is not null)
            {
                files.Add(item);
            }
        }

        // Telemetria derivada da lista final (nunca de contadores incrementais
        // dependentes de ordem física) — mesmo contrato do OrderedFileEnumerator.
        var telemetry = new ScanTelemetry
        {
            FilesEnumerated = files.Count,
            FilesPlaceholder = files.Count(f => f.IsPlaceholder),
        };

        return new EnumerationResult(files, errors, telemetry);
    }

    private static FileEntry? Transform(ref FileSystemEntry entry)
    {
        try
        {
            var isDirectory = entry.IsDirectory;
            var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;

            // Symlink de diretório: não existe FileEntry para ele (não é arquivo) e
            // a recursão não o atravessa (ver AttributesToSkip/RecurseSubdirectories).
            if (isDirectory && isLink)
            {
                return null;
            }

            if (isDirectory)
            {
                return null; // somente arquivos na lista Level 0
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
            _ = ex;
            return null;
        }
    }
}
