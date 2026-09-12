namespace Doctor.Core;

using System.Runtime.InteropServices;

/// <summary>
/// Linux file ID (card T07): inode via direct P/Invoke of <c>lstat(2)</c>.
/// Documented choice — NO Mono.Posix nor heavy dependency: a single interop
/// with caller-allocated buffer. The <c>stat</c> layout used is the glibc
/// Linux x86-64 one (dev_t=8B, ino_t=8B, ...); the target product is Windows,
/// and this path exists for Linux host development/testing (SPEC §5: avoid
/// extra stat — here lstat occurs only per file to obtain the inode that the
/// System.IO.Enumeration enumeration does not expose; on native Windows, the
/// file id comes from the FindFirstFileEx enumeration itself, with no extra call).
/// </summary>
public static class LinuxFileId
{
    public static string GetInode(string path)
    {
        return ReadInode(path).ToStringInvariant();
    }

    /// <summary>Returns (errno, inode). errno 0 = success.</summary>
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
                $"lstat failed for '{path}' (errno {Marshal.GetLastWin32Error()}).");
        }

        return st.st_ino;
    }

    private static string ToStringInvariant(this ulong value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // glibc struct stat on Linux x86-64 (64-bit): fields relevant to inode.
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
