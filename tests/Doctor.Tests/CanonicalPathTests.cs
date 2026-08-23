using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T17 (t_70329e55) — Canon de caminho e nomes hostis (SPEC §35 agente Security /
/// GATE 5 "path/reparse attacks testados"; ADR-0002 falha fechada; threat-model,
/// seção path traversal).
///
/// Matriz obrigatória do cartão: 12 casos hostis rejeitados por
/// <see cref="CanonicalPath.IsValidName"/> e 5 casos válidos aceitos, seguidos de
/// varredura integral dos nomes reservados NTFS (CON, PRN, AUX, NUL, COM1-9,
/// LPT1-9) nas três formas que o Windows reconhece (puro, minúsculo com extensão,
/// capitalizado) e do contrato de <see cref="CanonicalPath.Normalize"/>
/// (canonização de separadores, trunca­ção de extensão &gt; 255 chars e comparação
/// por <see cref="StringComparer.OrdinalIgnoreCase"/>).
/// </summary>
public class CanonicalPathTests
{
    // ---- Matriz do cartão: exatamente 12 nomes hostis, todos rejeitados ------

    [Theory]
    [InlineData(null)]                       // nulo
    [InlineData("")]                         // vazio
    [InlineData("relat\u0001orio.txt")]      // caractere de controle embutido
    [InlineData("fotos//viagem.jpg")]        // sequência // no meio
    [InlineData("a/../b.txt")]               // .. no meio
    [InlineData("../escape.txt")]            // .. no início
    [InlineData("backup\\..\\alvo.txt")]     // .. com separador Windows
    [InlineData("CON")]                      // reservado NTFS puro
    [InlineData("prn.txt")]                  // reservado + extensão, minúsculo
    [InlineData("aux.mp3")]                  // reservado + extensão
    [InlineData("NUL")]                      // reservado NTFS puro
    [InlineData("lpt7.xlsx")]                // faixa LPT + extensão
    public void IsValidName_NomeHostil_Rejeita(string? path)
    {
        Assert.False(CanonicalPath.IsValidName(path));
    }

    // ---- Matriz do cartão: exatamente 5 nomes válidos, todos aceitos ---------

    [Theory]
    [InlineData("relatorio.xlsx")]
    [InlineData("pasta/arquivo.txt")]                            // relativo aninhado
    [InlineData("Foto de praia.JPG")]                            // espaços + caixa
    [InlineData("conferencia.backup.sb-a3f19c.docx")]            // stem "con…" não é reservado
    [InlineData("dados.2026/v2/notas.md")]                       // múltiplos segmentos pontuados
    public void IsValidName_NomeValido_Aceita(string path)
    {
        Assert.True(CanonicalPath.IsValidName(path));
    }

    // ---- Varredura integral da tabela reservada NTFS --------------------------
    // O cartão lista as famílias com "como": a tabela completa tem 22 nomes.
    // Cada um entra nas três formas que o Windows trata como reservadas:
    // puro, minúsculo com extensão e capitalizado com extensão.

    public static IEnumerable<object[]> NomesReservadosComVariacoes()
    {
        string[] bases =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        foreach (var nome in bases)
        {
            yield return new object[] { nome };                                  // CON
            yield return new object[] { nome.ToLowerInvariant() + ".txt" };      // con.txt
            yield return new object[] { nome[..1].ToUpperInvariant() + nome[1..].ToLowerInvariant() + ".dat" }; // Con.dat, Prn.dat...
        }
    }

    [Theory]
    [MemberData(nameof(NomesReservadosComVariacoes))]
    public void IsValidName_TabelaReservadaCompleta_Rejeita(string path)
    {
        Assert.False(CanonicalPath.IsValidName(path));
    }

    // ---- Bordas que reforçam a regra do stem antes do primeiro ponto ----------

    [Theory]
    [InlineData("CON/planilha.xlsx")]               // segmento diretório reservado também vale
    [InlineData("pasta/CON.txt")]                   // reservado como último segmento
    [InlineData("CON.")]                            // ponto final: stem continua reservado
    public void IsValidName_ReservadoEmQualquerSegmento_Rejeita(string path)
    {
        Assert.False(CanonicalPath.IsValidName(path));
    }

    // Saneamento NTFS: o Win32 descarta ponto/espaço finais na criação
    // ("arquivo.txt." vira "arquivo.txt") — vetor de confusão de caminho;
    // o canon falha fechado em vez de aprovar nome que o volume reescreve.

    [Theory]
    [InlineData("arquivo.txt.")]
    [InlineData("arquivo.txt ")]
    [InlineData("pasta/notas .")]
    public void IsValidName_TerminaComPontoOuEspaco_Rejeita(string path)
    {
        Assert.False(CanonicalPath.IsValidName(path));
    }

    [Theory]
    [InlineData("conteudo.txt")]                    // prefixo de reservado não é reservado
    [InlineData("nullify.bin")]
    [InlineData("auxiliar.csv")]
    [InlineData("com10.txt")]                       // fora da faixa COM1-9
    [InlineData("lpt0.log")]                        // fora da faixa LPT1-9
    [InlineData("a/b/conferencia.txt")]
    [InlineData("documentos CON/planilha.xlsx")]    // reservado é nome EXATO, não substring
    public void IsValidName_ParecidoMasNaoReservado_Aceita(string path)
    {
        Assert.True(CanonicalPath.IsValidName(path));
    }

    // ---- Normalize: contrato ---------------------------------------------------

    [Fact]
    public void Normalize_SeparadoresWindows_CanonizaParaBarraUnix()
    {
        Assert.Equal("Pasta/Sub/Arquivo.TXT", CanonicalPath.Normalize("Pasta\\Sub\\Arquivo.TXT"));
    }

    [Fact]
    public void Normalize_ResultadosComparamPorOrdinalIgnoreCase()
    {
        // Contrato do cartão: igualdade de chave canônica é case-insensitive
        // (semântica NTFS), sempre ORDINAL — sem locale (espírito do SPEC §3).
        // Distinto de PathOrder.Comparer (ordenação de relatório, Ordinal sensível).
        var a = CanonicalPath.Normalize("Notas/Finais.TXT");
        var b = CanonicalPath.Normalize("notas/finais.txt");

        Assert.Equal(a, b, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(a, b, StringComparer.Ordinal); // canon preserva a caixa original
    }

    [Fact]
    public void Normalize_ExtensaoAcimaDe255_TrunciaPara255EGateAprova()
    {
        var hostil = "arquivo." + new string('a', 300);

        // Gate falha fechado: não aprova componente que o NTFS não armazenaria…
        Assert.False(CanonicalPath.IsValidName(hostil));

        // …e o Normalizador é a rota de reparo: trunca a extensão a 255.
        var canon = CanonicalPath.Normalize(hostil);

        Assert.StartsWith("arquivo.", canon);
        var extensao = canon[(canon.LastIndexOf('.') + 1)..];
        Assert.Equal(255, extensao.Length);
        Assert.True(CanonicalPath.IsValidName(canon));
    }

    [Fact]
    public void Normalize_Hostil_FalhaFechadaComArgumentException()
    {
        // Nunca sanitiza silenciosamente traversal nem reservado: lança.
        string? nulo = null;
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize(nulo!));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize(""));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize("a//b"));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize("x/../y"));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize("CON"));
        Assert.Throws<ArgumentException>(() => CanonicalPath.Normalize("travamento\u0007.txt"));
    }

    [Fact]
    public void Normalize_SaidaPassaNoGate_ParaOsCincoValidosDoCartao()
    {
        string[] validos =
        {
            "relatorio.xlsx",
            "pasta/arquivo.txt",
            "Foto de praia.JPG",
            "conferencia.backup.sb-a3f19c.docx",
            "dados.2026/v2/notas.md",
        };

        foreach (var nome in validos)
        {
            var canon = CanonicalPath.Normalize(nome);
            Assert.True(CanonicalPath.IsValidName(canon));
        }
    }
}
