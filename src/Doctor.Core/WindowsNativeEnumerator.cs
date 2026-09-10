namespace Doctor.Core;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Enumerador Level 0 nativo do Windows (SPEC §5, §33): P/Invoke FindFirstFileExW
/// com FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH (0x2). Metadados na própria
/// enumeração — zero stat extra. FileId NTFS 128-bit obtido via
/// GetFileInformationByHandleEx(FileIdInfo). VolumeId = volume GUID da raiz.
///
/// Contratos fixos:
///   - nunca atravessa reparse point de diretório (folha registrada em Errors);
///   - placeholders marcados via PlaceholderPolicy.IsPlaceholder (mesmos bits);
///   - saída ordenada por PathOrder (determinismo byte-a-byte).
///
/// Compila em qualquer plataforma; corpo só existe em #if WINDOWS.
/// Em Linux, instanciar lança PlatformNotSupportedException — use CrossPlatformEnumerator.
/// </summary>
public sealed class WindowsNativeEnumerator : IFileEnumerator
{
#if WINDOWS

    private const int FindExInfoBasic = 1;

    /// <summary>
    /// FINDEX_SEARCH_OPS.FindExSearchNameMatch — retorna TODAS as entradas.
    ///
    /// O codigo passava FindExSearchLimitToDirectories (valor 2) neste
    /// parametro, que instrui o Windows a devolver SOMENTE DIRETORIOS. Num
    /// enumerador de arquivos o efeito era: na raiz so os subdiretorios eram
    /// vistos (e empilhados), nenhum arquivo entrava na lista, e a varredura
    /// terminava com ZERO arquivos e ZERO erros — falha silenciosa. Como o
    /// corpo nativo nunca foi compilado (`#if WINDOWS` sem o simbolo definido),
    /// isso jamais apareceu.
    /// </summary>
    private const int FindExSearchNameMatch = 0;
    private const uint FIND_FIRST_EX_LARGE_FETCH = 0x2;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    private const int FileIdInfo = 18;

    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        var files = new List<FileEntry>();
        var errors = new List<ScanError>();

        // Normaliza caminho para formato Windows.
        var raizNormalizada = rootPath.Replace('/', '\\').TrimEnd('\\', '/');

        // Obtém volume GUID uma única vez.
        var volumeId = ObtemVolumeGuid(raizNormalizada);

        // Enumeração recursiva com pilha explícita (evita stack overflow).
        var pilha = new Stack<(string Caminho, int Profundidade)>();
        pilha.Push((raizNormalizada, 0));

        while (pilha.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (caminhoAtual, profundidade) = pilha.Pop();

            if (!EnumeraDiretorio(caminhoAtual, raizNormalizada, volumeId, profundidade, files, errors, ref pilha))
            {
                errors.Add(new ScanError(caminhoAtual, $"Erro ao acessar diretório '{caminhoAtual}'"));
            }
        }

        // Ordenação canônica byte-a-byte (contrato de determinismo §3).
        var ordenados = files
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => e with
            {
                IsPlaceholder = PlaceholderPolicy.IsPlaceholder(e),
                PlaceholderKind = PlaceholderPolicy.Classify(e),
                IsReparsePoint = (e.Attributes & FileAttributes.ReparsePoint) != 0,
            })
            .ToArray();

        var telemetry = new ScanTelemetry
        {
            FilesEnumerated = ordenados.Length,
            FilesPlaceholder = ordenados.Count(f => f.IsPlaceholder),
        };

        return new EnumerationResult(ordenados, errors, telemetry);
    }

    /// <summary>
    /// Enumera um único diretório usando FindFirstFileExW.
    /// Retorna false se não conseguir abrir o diretório.
    /// </summary>
    private static bool EnumeraDiretorio(
        string caminho,
        string raizNormalizada,
        string volumeId,
        int profundidade,
        List<FileEntry> files,
        List<ScanError> errors,
        ref Stack<(string Caminho, int Profundidade)> pilha)
    {
        var caminhoWin32 = "\\\\?\\" + caminho.Replace('/', '\\');
        var padraoBusca = caminhoWin32 + "\\*";

        var findData = new WIN32_FIND_DATAW();
        var hFind = NativeMethods.FindFirstFileExW(
            padraoBusca,
            FindExInfoBasic,
            out findData,
            FindExSearchNameMatch,
            IntPtr.Zero,
            FIND_FIRST_EX_LARGE_FETCH);

        if (!hFind.Equals(INVALID_HANDLE_VALUE))
        {
            using var safeFindHandle = new SafeFindHandle(hFind);

            try
            {
                do
                {
                    var nome = findData.cFileName;

                    if (nome == "." || nome == "..")
                    {
                        continue;
                    }

                    var caminhoCompleto = caminho + "\\" + nome;
                    var atributos = (FileAttributes)findData.dwFileAttributes;

                    var eDiretorio = (atributos & FileAttributes.Directory) != 0;
                    var ehReparse = (atributos & FileAttributes.ReparsePoint) != 0;

                    if (eDiretorio && ehReparse)
                    {
                        // Regra 4 do ADR-0004: diretório com reparse é SEMPRE folha.
                        errors.Add(new ScanError(
                            caminhoCompleto,
                            "reparse point de diretorio (junction/symlink) nao atravessado - folha registrada (ADR-0004 regra 4; threat-model T-02)"));
                        continue;
                    }

                    if (eDiretorio && !ehReparse)
                    {
                        // Empilha para processamento posterior.
                        pilha.Push((caminhoCompleto, profundidade + 1));
                        continue;
                    }

                    if (!eDiretorio)
                    {
                        // Arquivo: obtém file ID.
                        var fileId = ObtemFileId(caminhoCompleto, atributos);

                        var entrada = new FileEntry
                        {
                            Path = caminhoCompleto,
                            Size = findData.nFileSizeLow | ((long)findData.nFileSizeHigh << 32),
                            MtimeUtc = FileTimeToDateTimeUtc(findData.ftLastWriteTime),
                            Attributes = atributos,
                            VolumeId = volumeId,
                            FileId = fileId,
                        };

                        files.Add(entrada);
                    }
                }
                while (NativeMethods.FindNextFileW(safeFindHandle, out findData));
            }
            finally
            {
                safeFindHandle.Dispose();
            }
        }

        return true;
    }

    /// <summary>
    /// Identidade do volume da raiz: o NUMERO DE SERIE, via GetVolumeInformationW.
    ///
    /// Duas correcoes em relacao a versao anterior:
    ///
    /// 1. Chamava 'GetVolumeInformationForRootW', que nao existe em kernel32.dll
    ///    (EntryPointNotFoundException em toda invocacao).
    /// 2. Devolvia o conteudo de lpVolumeNameBuffer, que e o ROTULO do volume
    ///    ("Windows", "Dados"), nao o GUID que a documentacao prometia. Rotulo e
    ///    editavel pelo usuario e pode repetir entre volumes: nao serve como
    ///    identidade.
    ///
    /// O numero de serie e o que efetivamente identifica o volume e e o mesmo
    /// campo que FILE_ID_INFO pareia com o FileId de 128 bits — a chave
    /// (VolumeId, FileId) usada na deteccao de ciclo de OrderedFileEnumerator.
    /// </summary>
    private static string ObtemVolumeGuid(string raizWin32)
    {
        // GetVolumeInformationW exige a RAIZ DO VOLUME ("C:\"), nao um diretorio
        // qualquer: passar o caminho da varredura faz a chamada falhar e cair no
        // fallback de MachineName, perdendo a distincao entre volumes.
        var raizVolume = Path.GetPathRoot(raizWin32);
        if (string.IsNullOrEmpty(raizVolume))
        {
            return Environment.MachineName;
        }

        var volumeNameBuffer = new char[261];   // MAX_PATH + 1
        var fsNameBuffer = new char[261];
        var success = NativeMethods.GetVolumeInformationW(
            raizVolume,
            volumeNameBuffer,
            (uint)volumeNameBuffer.Length,
            out var volumeSerialNumber,
            out _,
            out _,
            fsNameBuffer,
            (uint)fsNameBuffer.Length);

        if (!success)
        {
            // Degrada para um identificador estavel da maquina em vez de lancar:
            // sem volume id o scan ainda roda, apenas com dedup por maquina.
            return Environment.MachineName;
        }

        return volumeSerialNumber.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Obtém o NTFS FileId (128-bit) do arquivo usando GetFileInformationByHandleEx.
    /// Em caso de falha, retorna hash do caminho como fallback.
    /// </summary>
    private static string ObtemFileId(string caminho, FileAttributes atributos)
    {
        // Symlinks e reparse points não podem ter handle aberto normalmente.
        if ((atributos & FileAttributes.ReparsePoint) != 0)
        {
            return HashCaminho(caminho);
        }

        var win32Path = "\\\\?\\" + caminho.Replace('/', '\\');

        try
        {
            var hFile = NativeMethods.CreateFileW(
                win32Path,
                0, // dwDesiredAccess = 0 (somente obter info)
                (uint)FileShare.Read,
                IntPtr.Zero,
                3, // OPEN_EXISTING
                0,
                IntPtr.Zero);

            if (hFile == INVALID_HANDLE_VALUE)
            {
                return HashCaminho(caminho);
            }

            using var safeHandle = new SafeFileHandle(hFile, true);

            // FILE_ID_128: 8 bytes VolumeSerial + 16 bytes FileId = 24 bytes total
            var buffer = Marshal.AllocHGlobal(24);
            try
            {
                var success = NativeMethods.GetFileInformationByHandleEx(
                    safeHandle,
                    FileIdInfo,
                    buffer,
                    24);

                if (!success)
                {
                    return HashCaminho(caminho);
                }

                // Copia os 16 bytes do FileId (offset 8)
                var fileIdBytes = new byte[16];
                Marshal.Copy(IntPtr.Add(buffer, 8), fileIdBytes, 0, 16);

                return BitConverter.ToString(fileIdBytes).Replace("-", "").ToLowerInvariant();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return HashCaminho(caminho);
        }
    }

    private static string HashCaminho(string caminho)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(caminho));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static DateTimeOffset FileTimeToDateTimeUtc(FILETIME fileTime)
    {
        var ticks = ((long)fileTime.dwHighDateTime << 32) | fileTime.dwLowDateTime;
        return DateTimeOffset.FromFileTime(ticks);
    }

#else

    public EnumerationResult Enumerate(string rootPath, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(
            "WindowsNativeEnumerator é exclusivo de Windows (build #if WINDOWS). " +
            "Em Linux, use CrossPlatformEnumerator.");

#endif
}

/// <summary>
/// Interop com Windows API para o enumerador nativo.
/// </summary>
#if WINDOWS
internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstFileExW(
        string lpFileName,
        int fInfoId,
        out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        uint dwAdditionalFlags);

    // ATENCAO: nao existe 'FindNextFileExW' em kernel32.dll.
    //
    // A continuacao de uma enumeracao aberta por FindFirstFileEx e feita por
    // FindNextFileW, que recebe APENAS DOIS argumentos (handle e buffer): o
    // nivel de informacao e a operacao de busca sao fixados na chamada
    // FindFirstFileEx e nao se repetem. A declaracao anterior inventava uma
    // funcao com quatro parametros. Nunca foi compilada, entao o erro so
    // existia no papel.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
               EntryPoint = "FindNextFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFileW(
        SafeFindHandle hFindFile,
        out WIN32_FIND_DATAW lpFindFileData);

    // Recebe IntPtr, nao SafeFindHandle.
    //
    // Esta funcao e chamada de dentro de SafeFindHandle.ReleaseHandle(), ou
    // seja, DURANTE o descarte do proprio handle. Marshalar um SafeHandle nesse
    // momento faz o runtime tentar DangerousAddRef num objeto ja em fechamento,
    // e a chamada morre com ObjectDisposedException — a enumeracao nativa
    // inteira falhava no primeiro diretorio por causa disto. A sobrecarga com
    // IntPtr fecha o handle cru, que e o contrato esperado de ReleaseHandle.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        int dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandleEx(
        SafeHandle hFile,
        int fileInfoClass,
        IntPtr lpBuffer,
        uint dwBufferSize);

    // ATENCAO: a entrada correta e GetVolumeInformationW.
    //
    // Este P/Invoke declarava 'GetVolumeInformationForRootW' — funcao que NAO
    // EXISTE em kernel32.dll. A assinatura abaixo e, byte a byte, a de
    // GetVolumeInformationW; so o nome estava errado. Como o corpo nativo vivia
    // sob `#if WINDOWS` e o simbolo nunca era definido (ver Directory.Build.props),
    // este codigo jamais foi compilado nem executado, e o erro so aparecia em
    // runtime como EntryPointNotFoundException. Todos os sete testes de
    // WindowsNativeEnumeratorTests falhavam por esta unica linha.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
               EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(
        string rootPath,
        [Out] char[] volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        [Out] char[] fileSystemNameBuffer,
        uint fileSystemNameSize);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WIN32_FIND_DATAW
{
    public uint dwFileAttributes;
    public FILETIME ftCreationTime;
    public FILETIME ftLastAccessTime;
    public FILETIME ftLastWriteTime;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint dwReserved0;
    public uint dwReserved1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string cFileName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
    public string cAlternateFileName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILETIME
{
    public uint dwLowDateTime;
    public uint dwHighDateTime;
}

internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle(IntPtr preexistingHandle) : base(true)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle()
    {
        // `handle` (o IntPtr cru), nunca `this`: passar o proprio SafeHandle
        // aqui provoca ObjectDisposedException, pois o objeto ja esta sendo
        // descartado quando ReleaseHandle roda.
        return NativeMethods.FindClose(handle);
    }
}
#endif
