namespace Doctor.Core;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Native Windows Level 0 enumerator (SPEC §5, §33): P/Invoke FindFirstFileExW
/// with FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH (0x2). Metadata in the
/// enumeration itself — zero extra stat. 128-bit NTFS FileId obtained via
/// GetFileInformationByHandleEx(FileIdInfo). VolumeId = root's volume GUID.
///
/// Fixed contracts:
///   - never crosses directory reparse points (leaf registered in Errors);
///   - placeholders marked via PlaceholderPolicy.IsPlaceholder (same bits);
///   - output sorted by PathOrder (byte-by-byte determinism).
///
/// Compiles on any platform; body only exists in #if WINDOWS.
/// On Linux, instantiation throws PlatformNotSupportedException — use CrossPlatformEnumerator.
/// </summary>
public sealed class WindowsNativeEnumerator : IFileEnumerator
{
#if WINDOWS

    private const int FindExInfoBasic = 1;

    /// <summary>
    /// FINDEX_SEARCH_OPS.FindExSearchNameMatch — returns ALL entries.
    ///
    /// The code previously passed FindExSearchLimitToDirectories (value 2) in this
    /// parameter, which instructed Windows to return ONLY DIRECTORIES. In a
    /// file enumerator the effect was: at root only subdirectories were
    /// seen (and pushed), no file entered the list, and the scan
    /// ended with ZERO files and ZERO errors — silent failure. Since the
    /// native body was never compiled (`#if WINDOWS` without the symbol defined),
    /// this never appeared.
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

        // Normalizes path to Windows format.
        var normalizedRoot = rootPath.Replace('/', '\\').TrimEnd('\\', '/');

        // Gets volume GUID once.
        var volumeId = GetVolumeGuid(normalizedRoot);

        // Recursive enumeration with explicit stack (avoids stack overflow).
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((normalizedRoot, 0));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (currentPath, depth) = stack.Pop();

            if (!EnumerateDirectory(currentPath, normalizedRoot, volumeId, depth, files, errors, ref stack))
            {
                errors.Add(new ScanError(currentPath, $"Error accessing directory '{currentPath}'"));
            }
        }

        // Byte-by-byte canonical ordering (determinism §3 contract).
        var ordered = files
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
            FilesEnumerated = ordered.Length,
            FilesPlaceholder = ordered.Count(f => f.IsPlaceholder),
        };

        return new EnumerationResult(ordered, errors, telemetry);
    }

    /// <summary>
    /// Enumerates a single directory using FindFirstFileExW.
    /// Returns false if the directory cannot be opened.
    /// </summary>
    private static bool EnumerateDirectory(
        string path,
        string normalizedRoot,
        string volumeId,
        int depth,
        List<FileEntry> files,
        List<ScanError> errors,
        ref Stack<(string Path, int Depth)> stack)
    {
        var win32Path = "\\\\?\\" + path.Replace('/', '\\');
        var searchPattern = win32Path + "\\*";

        var findData = new WIN32_FIND_DATAW();
        var hFind = NativeMethods.FindFirstFileExW(
            searchPattern,
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
                    var name = findData.cFileName;

                    if (name == "." || name == "..")
                    {
                        continue;
                    }

                    var fullPath = path + "\\" + name;
                    var attributes = (FileAttributes)findData.dwFileAttributes;

                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var isReparse = (attributes & FileAttributes.ReparsePoint) != 0;

                    if (isDirectory && isReparse)
                    {
                        // ADR-0004 rule 4: directory with reparse is ALWAYS a leaf.
                        errors.Add(new ScanError(
                            fullPath,
                            "directory reparse point (junction/symlink) not crossed — leaf registered (ADR-0004 rule 4; threat-model T-02)"));
                        continue;
                    }

                    if (isDirectory && !isReparse)
                    {
                        // Pushes for later processing.
                        stack.Push((fullPath, depth + 1));
                        continue;
                    }

                    if (!isDirectory)
                    {
                        // File: obtains file ID.
                        var fileId = GetFileId(fullPath, attributes);

                        var entry = new FileEntry
                        {
                            Path = fullPath,
                            Size = findData.nFileSizeLow | ((long)findData.nFileSizeHigh << 32),
                            MtimeUtc = FileTimeToDateTimeUtc(findData.ftLastWriteTime),
                            Attributes = attributes,
                            VolumeId = volumeId,
                            FileId = fileId,
                        };

                        files.Add(entry);
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
    /// Root volume identity: SERIAL NUMBER, via GetVolumeInformationW.
    ///
    /// Two fixes relative to the previous version:
    ///
    /// 1. Previously called 'GetVolumeInformationForRootW', which does not exist in kernel32.dll
    ///    (EntryPointNotFoundException on every invocation).
    /// 2. Returned the content of lpVolumeNameBuffer, which is the volume LABEL
    ///    ("Windows", "Dados"), not the GUID the documentation promised. Label is
    ///    user-editable and can repeat across volumes: not usable as identity.
    ///
    /// The serial number is what effectively identifies the volume and is the same
    /// field that FILE_ID_INFO pairs with the 128-bit FileId — the key
    /// (VolumeId, FileId) used in OrderedFileEnumerator's cycle detection.
    /// </summary>
    private static string GetVolumeGuid(string win32Root)
    {
        // GetVolumeInformationW requires the VOLUME ROOT ("C:\"), not an arbitrary
        // directory: passing the scan path makes the call fail and fall back to
        // MachineName, losing distinction between volumes.
        var volumeRoot = Path.GetPathRoot(win32Root);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            return Environment.MachineName;
        }

        var volumeNameBuffer = new char[261];   // MAX_PATH + 1
        var fsNameBuffer = new char[261];
        var success = NativeMethods.GetVolumeInformationW(
            volumeRoot,
            volumeNameBuffer,
            (uint)volumeNameBuffer.Length,
            out var volumeSerialNumber,
            out _,
            out _,
            fsNameBuffer,
            (uint)fsNameBuffer.Length);

        if (!success)
        {
            // Degrades to a stable machine identifier instead of throwing:
            // without volume id the scan still runs, just with per-machine dedup.
            return Environment.MachineName;
        }

        return volumeSerialNumber.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Obtains the 128-bit NTFS FileId using GetFileInformationByHandleEx.
    /// On failure, returns a hash of the path as fallback.
    /// </summary>
    private static string GetFileId(string path, FileAttributes attributes)
    {
        // Symlinks and reparse points cannot have a normally opened handle.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return HashPath(path);
        }

        var win32Path = "\\\\?\\" + path.Replace('/', '\\');

        try
        {
            var hFile = NativeMethods.CreateFileW(
                win32Path,
                0, // dwDesiredAccess = 0 (query only)
                (uint)FileShare.Read,
                IntPtr.Zero,
                3, // OPEN_EXISTING
                0,
                IntPtr.Zero);

            if (hFile == INVALID_HANDLE_VALUE)
            {
                return HashPath(path);
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
                    return HashPath(path);
                }

                // Copies the 16 bytes of FileId (offset 8)
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
            return HashPath(path);
        }
    }

    private static string HashPath(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path));
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
            "WindowsNativeEnumerator is Windows-only (build #if WINDOWS). " +
            "On Linux, use CrossPlatformEnumerator.");

#endif
}

/// <summary>
/// Windows API interop for the native enumerator.
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

    // NOTE: 'FindNextFileExW' does not exist in kernel32.dll.
    //
    // The continuation of an enumeration opened by FindFirstFileEx is done by
    // FindNextFileW, which takes ONLY TWO arguments (handle and buffer): the
    // information level and search operation are fixed in the
    // FindFirstFileEx call and are not repeated. The previous declaration invented
    // a function with four parameters. It was never compiled, so the error only
    // existed on paper.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
               EntryPoint = "FindNextFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFileW(
        SafeFindHandle hFindFile,
        out WIN32_FIND_DATAW lpFindFileData);

    // Takes IntPtr, not SafeFindHandle.
    //
    // This function is called from within SafeFindHandle.ReleaseHandle(), i.e.,
    // DURING the disposal of the handle itself. Marshaling a SafeHandle at this
    // point makes the runtime try DangerousAddRef on an object already closing,
    // and the call dies with ObjectDisposedException — the entire native
    // enumeration failed at the first directory because of this. The IntPtr overload
    // closes the raw handle, which is the expected contract of ReleaseHandle.
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

    // NOTE: the correct entry is GetVolumeInformationW.
    //
    // This P/Invoke previously declared 'GetVolumeInformationForRootW' — a function that
    // DOES NOT EXIST in kernel32.dll. The signature below is, byte by byte, that of
    // GetVolumeInformationW; only the name was wrong. Since the native body lived
    // under `#if WINDOWS` and the symbol was never defined (see Directory.Build.props),
    // this code was never compiled or executed, and the error only appeared at
    // runtime as EntryPointNotFoundException. All seven
    // WindowsNativeEnumeratorTests failed because of this single line.
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
        // `handle` (the raw IntPtr), never `this`: passing the SafeHandle itself
        // here causes ObjectDisposedException, because the object is already being
        // disposed when ReleaseHandle runs.
        return NativeMethods.FindClose(handle);
    }
}
#endif
