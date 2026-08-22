namespace Doctor.Core;

using System.Runtime.InteropServices;

/// <summary>
/// File ID em Linux (card T07): inode via P/Invoke direto de <c>lstat(2)</c>.
/// Escolha documentada — SEM Mono.Posix nem dependência pesada: um único interop
/// com buffer alocado pelo chamador. O layout do <c>stat</c> usado é o do glibc
/// Linux x86-64 (dev_t=8B, ino_t=8B, ...); o produto alvo é Windows, e este caminho
/// existe para desenvolvimento/teste no host Linux (SPEC §5: evitar stat extra —
/// aqui o lstat só ocorre por arquivo para obter o inode que a enumeração
/// System.IO.Enumeration não expõe; no Windows nativo, o file id vem da própria
/// enumeração FindFirstFileEx, sem chamada extra).
/// </summary>
public static class LinuxFileId
{
    public static string GetInode(string path)
    {
        return ReadInode(path).ToStringInvariant();
    }

    /// <summary>Retorna (errno, inode). errno 0 = sucesso.</summary>
    internal static (int Errno, ulong Inode) TryGetInode(string path)
    {
        var st = new Stat();
        var rc = lstat(path, ref st);
        return (rc == 0 ? 0 : Marshal.GetLastWin32Error(), st.st_ino);
    }

    private static ulong ReadInode(string path)
    {
        var st = new Stat();
        if (lstat(path, ref st) != 0)
        {
            throw new IOException(
                $"lstat falhou para '{path}' (errno {Marshal.GetLastWin32Error()}).");
        }

        return st.st_ino;
    }

    private static string ToStringInvariant(this ulong value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // struct stat do glibc em Linux x86-64 (bits 64): campos relevantes ao inode.
    [StructLayout(LayoutKind.Sequential)]
    private struct Stat
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public uint st_uid;
        public uint st_gid;
        public int __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atime_sec;
        public long st_atime_nsec;
        public long st_mtime_sec;
        public long st_mtime_nsec;
        public long st_ctime_sec;
        public long st_ctime_nsec;
        public long __unused_4;
        public long __unused_5;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int lstat(string path, ref Stat statbuf);
}
