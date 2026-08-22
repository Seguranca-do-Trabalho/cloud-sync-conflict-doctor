using System.Runtime.InteropServices;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T07 — CrossPlatformEnumerator sobre árvore REAL (System.IO.Enumeration.FileSystemEnumerable,
/// metadados sem stat extra): coleta recursiva de arquivos, FileId = inode via lstat P/Invoke,
/// symlinks marcados como ReparsePoint (análogo POSIX) e nunca atravessados.
/// </summary>
public class CrossPlatformEnumeratorTests : IDisposable
{
    private readonly string _root;

    public CrossPlatformEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "t07-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Enumerate_ArvoreReal_OrdenadaCompletaComInode()
    {
        // Nomes escolhidos para que a ordem física de criação difira da ordem Ordinal.
        File.WriteAllBytes(Path.Combine(_root, "a.txt"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "B.txt"), [2]);
        File.WriteAllBytes(Path.Combine(_root, "_.txt"), [3]);
        File.WriteAllBytes(Path.Combine(_root, "á.txt"), [4]);   // UTF-8: 0xC3 0xA1
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "z.txt"), [5]);

        var result = new OrderedFileEnumerator(new CrossPlatformEnumerator()).Enumerate(_root, CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(5, result.Files.Count);

        var paths = result.Files.Select(e => e.Path).ToArray();
        // Ordem canônica byte-a-byte, recursiva, independente da ordem física do diretório.
        Assert.Equal(
            new[] { "B.txt", "_.txt", "a.txt", "sub/z.txt", "á.txt" }.Select(p => Path.Combine(_root, p)),
            paths);

        Assert.All(result.Files, e =>
        {
            Assert.True(long.TryParse(e.FileId, out var inode));
            Assert.True(inode > 0); // inode real obtido por lstat
            Assert.Equal(new FileInfo(e.Path).Length, e.Size);
        });
        Assert.Equal(5, result.Telemetry.FilesEnumerated);
    }

    [Fact]
    public void Enumerate_Symlink_NuncaAtravessado_E_MarcadoPlaceholder()
    {
        // Alvo FORA da árvore escaneada: se o enumerator atravessasse o link,
        // o arquivo alvo apareceria no resultado — a asserção abaixo pegaria.
        var outside = Path.Combine(Path.GetTempPath(), "t07-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllBytes(outside, [9, 9, 9]);

        try
        {
            File.WriteAllBytes(Path.Combine(_root, "real.txt"), [1]);
            var linkPath = Path.Combine(_root, "link.txt");
            Assert.True(symlink(outside, linkPath) == 0, "symlink(2) falhou no ambiente de teste");

            var result = new CrossPlatformEnumerator().Enumerate(_root, CancellationToken.None);

            var link = Assert.Single(result.Files, e => e.Path == linkPath);
            Assert.True(link.IsPlaceholder);
            Assert.Equal(PlaceholderKind.ReparsePoint, link.PlaceholderKind);
            Assert.DoesNotContain(result.Files, e => e.Path == outside);
            Assert.Equal(result.Files.Count(e => e.IsPlaceholder), result.Telemetry.FilesPlaceholder);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int symlink(string target, string linkpath);
}
