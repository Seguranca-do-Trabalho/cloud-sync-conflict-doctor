using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T23 (t_64c4d4c0) — Seleção de comparador por extensão case-insensitive
/// (ADR-0011 item 1; contratos.md IDocumentComparator): .txt/.log/.ini/.cfg/.conf
/// ⇒ texto; TODO o resto, inclusive SEM extensão, ⇒ BinaryFallbackComparator.
/// Markdown/CSV são cards futuros (06.2) — aqui caem no binário.
/// </summary>
[Trait("Category", "Comparison")]
public class ComparatorSelectorTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _arquivos = new();

    public ComparatorSelectorTests()
    {
        _dir = Directory.CreateTempSubdirectory("doctor-t23-sel-").FullName;
    }

    public void Dispose()
    {
        foreach (var a in _arquivos)
        {
            File.Delete(a);
        }

        Directory.Delete(_dir);
    }

    [Theory]
    [InlineData(".TXT")]
    [InlineData(".txt")]
    [InlineData(".Log")]
    [InlineData(".INI")]
    [InlineData(".Cfg")]
    [InlineData(".CONF")]
    public void Sel01_ExtensaoTexto_CaseInsensitive_RoteiaParaTextComparator(string extensao)
    {
        var (left, right) = ParComExtensao(extensao);

        var resultado = ComparatorSelector.Compare(left, right, CancellationToken.None);

        Assert.Equal("text", resultado.ComparatorKind);
    }

    [Theory]
    [InlineData(".md")]
    [InlineData(".csv")]
    [InlineData(".bin")]
    [InlineData(".docx")]
    [InlineData("")]
    public void Sel02_ExtensaoDesconhecidaOuAusente_RoteiaParaBinaryFallback(string extensao)
    {
        var (left, right) = ParComExtensao(extensao);

        var resultado = ComparatorSelector.Compare(left, right, CancellationToken.None);

        Assert.Equal("binary", resultado.ComparatorKind);
    }

    /// <summary>Cria par idêntico de arquivos com a extensão pedida.</summary>
    private (FileEntry Left, FileEntry Right) ParComExtensao(string extensao)
    {
        var nome = $"arq{extensao}";
        var pl = Path.Combine(_dir, $"L-{nome}");
        var pr = Path.Combine(_dir, $"R-{nome}");
        File.WriteAllBytes(pl, new byte[] { 0x61, 0x0A });
        File.WriteAllBytes(pr, new byte[] { 0x61, 0x0A });
        _arquivos.Add(pl);
        _arquivos.Add(pr);
        return (TextComparatorTests.Entrada(pl, 2), TextComparatorTests.Entrada(pr, 2));
    }
}
