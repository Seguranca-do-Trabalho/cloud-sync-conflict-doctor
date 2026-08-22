namespace Doctor.Core;

/// <summary>
/// STUB DOCUMENTADO — enumerador Windows-native (card futuro do agente windows-native,
/// SPEC §5/§33). NÃO implementado neste card: o alvo aqui é o protótipo Level 0 com
/// semântica e contratos fechados, compilável no host de desenvolvimento Linux.
///
/// Contrato já fixado para o implementador futuro:
///   - P/Invoke FindFirstFileExW com infoLevel = FindExInfoBasic e
///     additionalFlags = FIND_FIRST_EX_LARGE_FETCH (0x2) — metadados na enumeração,
///     zero stat extra;
///   - file id (NTFS FILE_ID / 128-bit) obtido da própria enumeração ou via
///     OpenFileById/GetFileInformationByHandleEx(FileIdInfo) — nunca por caminho;
///   - VolumeId = volume GUID ("\\?\Volume{...}") da raiz escaneada;
///   - placeholders detectados pelos MESMOS bits de PlaceholderPolicy — a marcação
///     permanece centralizada em PlaceholderPolicy.IsPlaceholder, que este stub já usa;
///   - nunca seguir junctions/symlinks de diretório; nunca abrir placeholder.
///
/// A classe compila em qualquer plataforma; o corpo só existe em build Windows
/// (#if WINDOWS), onde lançar NotImplemented explicito até o card nativo chegar.
/// Em Linux, instanciar esta classe é erro de programação: use CrossPlatformEnumerator.
/// </summary>
public sealed class WindowsNativeEnumerator : IFileEnumerator
{
#if WINDOWS
    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
        => throw new NotImplementedException(
            "WindowsNativeEnumerator: P/Invoke FindFirstFileEx (FindExInfoBasic + " +
            "FIND_FIRST_EX_LARGE_FETCH) será implementado pelo card windows-native. " +
            "Use CrossPlatformEnumerator enquanto isso.");
#else
    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(
            "WindowsNativeEnumerator é exclusivo de Windows (build #if WINDOWS). " +
            "Em Linux, use CrossPlatformEnumerator.");
#endif
}
