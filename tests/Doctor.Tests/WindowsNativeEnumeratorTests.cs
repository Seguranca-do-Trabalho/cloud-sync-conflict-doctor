using System.Runtime.InteropServices;
using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_f706caaa) — WindowsNativeEnumerator: testes de equivalência, estabilidade
/// de FileId/VolumeId, placeholder detection e reparse point handling.
///
/// Todos os testes são marcados com Trait("OS", "Windows") para execução apenas
/// em runners Windows do CI. No Linux, serão pulados.
/// </summary>
[Trait("OS", "Windows")]
public class WindowsNativeEnumeratorTests : IDisposable
{
    private readonly string _root;

    public WindowsNativeEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "t23-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Enumerate_EquivalenciaComCrossPlatform_MesmosCaminhosEmOrdemOrdinal()
    {
#if !WINDOWS
        // Teste marcado com Trait("OS", "Windows") - não compila no Linux.
        return;
#endif

        CriarArvoreFixture();

        var enumeratorWindows = new OrderedFileEnumerator(new WindowsNativeEnumerator());
        var enumeratorXplat = new OrderedFileEnumerator(new CrossPlatformEnumerator());

        var resultadoWindows = enumeratorWindows.Enumerate(_root, CancellationToken.None);
        var resultadoXplat = enumeratorXplat.Enumerate(_root, CancellationToken.None);

        Assert.Equal(resultadoXplat.Files.Count, resultadoWindows.Files.Count);

        var caminhosWindows = resultadoWindows.Files.Select(f => f.Path).ToArray();
        var caminhosXplat = resultadoXplat.Files.Select(f => f.Path).ToArray();

        Assert.Equal(caminhosXplat.OrderBy(p => p, StringComparer.Ordinal),
                     caminhosWindows.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Enumerate_FileIdNaoVazio_EstableEntreVarreduras()
    {
#if !WINDOWS
        // Teste marcado com Trait("OS", "Windows") - não compila no Linux.
        return;
#endif

        CriarArvoreFixture();

        var enumerator = new WindowsNativeEnumerator();

        var primeiro = enumerator.Enumerate(_root, CancellationToken.None);
        var segundo = enumerator.Enumerate(_root, CancellationToken.None);

        Assert.True(primeiro.Files.Count > 0, "Deve haver arquivos na árvore");

        Assert.All(primeiro.Files, f =>
        {
            Assert.False(string.IsNullOrEmpty(f.FileId), $"FileId vazio para {f.Path}");
            Assert.False(string.IsNullOrEmpty(f.VolumeId), $"VolumeId vazio para {f.Path}");
        });

        for (var i = 0; i < primeiro.Files.Count; i++)
        {
            Assert.Equal(primeiro.Files[i].FileId, segundo.Files[i].FileId);
            Assert.Equal(primeiro.Files[i].VolumeId, segundo.Files[i].VolumeId);
        }
    }

    [Fact]
    public void Enumerate_PlaceholderMarcado_SemAbrirConteudo()
    {
#if WINDOWS
        var caminhoArquivo = Path.Combine(_root, "offline.txt");
        File.WriteAllText(caminhoArquivo, "conteúdo offline");

        var winPath = caminhoArquivo.Replace('/', '\\');
        NativeMethods.SetFileAttributesW(winPath, (uint)FileAttributes.Offline);

        try
        {
            var resultado = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

            var entrada = Assert.Single(resultado.Files);
            Assert.True(entrada.IsPlaceholder);
            Assert.Equal(PlaceholderKind.Offline, entrada.PlaceholderKind);
        }
        finally
        {
            NativeMethods.SetFileAttributesW(winPath, (uint)FileAttributes.Normal);
        }
#else
        // Teste marcado com Trait("OS", "Windows") - não compila no Linux.
        return;
#endif
    }

    [Fact]
    public void Enumerate_ReparseDirectory_FolhaRegistradaEMarcada()
    {
#if WINDOWS
        var alvo = Path.Combine(_root, "alvo");
        Directory.CreateDirectory(alvo);
        File.WriteAllText(Path.Combine(alvo, "dentro.txt"), "x");

        var juncao = Path.Combine(_root, "juncao");
        NativeMethods.CreateSymbolicLinkW(juncao, alvo, true);

        try
        {
            var resultado = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

            var erro = Assert.Single(resultado.Errors, e => e.Path.Contains("juncao", StringComparison.Ordinal));
            Assert.Contains("reparse point", erro.Message, StringComparison.OrdinalIgnoreCase);

            // O que precisa ser garantido e que NADA seja alcancado ATRAVES da
            // juncao. A versao anterior exigia que "dentro.txt" nao aparecesse
            // em lugar nenhum — asercao incorreta, porque o alvo da juncao fica
            // DENTRO da raiz varrida (_root/alvo/dentro.txt) e deve mesmo ser
            // enumerado pelo caminho direto. O teste nunca rodou (o corpo estava
            // sob `#if !WINDOWS return;` com o simbolo WINDOWS jamais definido),
            // entao o engano passou despercebido.
            Assert.DoesNotContain(
                resultado.Files,
                f => f.Path.Contains("juncao", StringComparison.Ordinal));

            // ...e o alvo legitimo continua sendo enumerado pelo caminho real.
            Assert.Contains(
                resultado.Files,
                f => f.Path.EndsWith(Path.Combine("alvo", "dentro.txt"), StringComparison.Ordinal));
        }
        finally
        {
            NativeMethods.DeleteFileW(juncao);
        }
#else
        // Teste marcado com Trait("OS", "Windows") - não compila no Linux.
        return;
#endif
    }

    [Fact]
    public void Enumerate_SymlinkDeArquivo_MarcadoReparsePoint()
    {
#if WINDOWS
        var alvo = Path.Combine(_root, "real.txt");
        File.WriteAllText(alvo, "conteúdo");

        var link = Path.Combine(_root, "atalho.txt");
        NativeMethods.CreateSymbolicLinkW(link, alvo, false);

        try
        {
            var resultado = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

            var entrada = Assert.Single(resultado.Files, f => f.Path.Contains("atalho.txt", StringComparison.Ordinal));
            Assert.True(entrada.IsReparsePoint);
            Assert.True(entrada.IsPlaceholder);
            Assert.Equal(PlaceholderKind.ReparsePoint, entrada.PlaceholderKind);
        }
        finally
        {
            NativeMethods.DeleteFileW(link);
        }
#else
        // Teste marcado com Trait("OS", "Windows") - não compila no Linux.
        return;
#endif
    }

    [Fact]
    public void Enumerate_TelemetriaCoerente()
    {
#if !WINDOWS
        // Teste marcado com Trait("OS", "Windows") - não compila no Linux.
        return;
#endif

        CriarArvoreFixture();

        var resultado = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

        Assert.Equal(resultado.Files.Count, resultado.Telemetry.FilesEnumerated);
        Assert.Equal(resultado.Files.Count(f => f.IsPlaceholder), resultado.Telemetry.FilesPlaceholder);
    }

    [Fact]
    public void Enumerate_OrdemDeterministica()
    {
#if !WINDOWS
        // Teste marcado com Trait("OS", "Windows") - não compila no Linux.
        return;
#endif

        File.WriteAllText(Path.Combine(_root, "z.txt"), "z");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "m.txt"), "m");

        var resultado1 = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);
        var resultado2 = new WindowsNativeEnumerator().Enumerate(_root, CancellationToken.None);

        var paths1 = resultado1.Files.Select(f => f.Path).ToArray();
        var paths2 = resultado2.Files.Select(f => f.Path).ToArray();

        Assert.Equal(paths1, paths2);
        Assert.Equal(new[] { "a.txt", "m.txt", "z.txt" }.Select(p => Path.Combine(_root, p)),
                     paths1.OrderBy(p => p, StringComparer.Ordinal));
    }

    private void CriarArvoreFixture()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "B.txt"), "B");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "z.txt"), "sub/z");
    }
}

// Helpers P/Invoke para testes.
#if WINDOWS
internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateSymbolicLinkW(string lpSymlinkFileName, string lpTargetFileName, bool bIsDirectory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteFileW(string lpFileName);
}
#endif
