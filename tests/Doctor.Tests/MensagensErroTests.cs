using Doctor.Gui;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G2 (t_87f625aa) — mensagens de erro padrão do §37 como recurso CENTRALIZADO
/// (src/Doctor.Gui/MensagensErro.cs): sem jargão técnico e sem culpar o usuário.
/// Regras verificadas: determinismo (mesma instância constante), ausência de
/// vocabulário técnico/de culpabilização e ausência de vocabulário destrutivo
/// (a guarda GUIVM-02 também varre este arquivo).
/// </summary>
public class MensagensErroTests
{
    private static readonly string[] JargaoProibido =
    [
        "exceção", "exception", "stack", "erro interno", "código de erro",
        "null", "api", "timeout", "log de", "0x",
    ];

    private static readonly string[] CulpaProibida =
    [
        "inválido", "inválida", "você errou", "digitou errado", "esqueceu",
        "culpa sua", "proibido",
    ];

    private static readonly (string Nome, string Valor)[] Todas =
    [
        ("PastaNaoDisponivel", MensagensErro.PastaNaoDisponivel),
        ("NenhumItemSelecionado", MensagensErro.NenhumItemSelecionado),
        ("MovimentacaoNaoConcluida", MensagensErro.MovimentacaoNaoConcluida),
        ("FalhaInesperada", MensagensErro.FalhaInesperada),
    ];

    [Fact]
    public void Toda_mensagem_existe_e_nao_e_vazia()
    {
        foreach (var (nome, valor) in Todas)
        {
            Assert.False(string.IsNullOrWhiteSpace(valor),
                $"Mensagem de erro {nome} está vazia.");
        }
    }

    [Fact]
    public void Mensagens_sao_constantes_deterministicas()
    {
        // Mesma referência em chamadas distintas: nenhuma hora local, locale ou
        // aleatoriedade na formação do texto (determinismo §3).
        Assert.Same(MensagensErro.PastaNaoDisponivel, MensagensErro.PastaNaoDisponivel);
        Assert.Same(MensagensErro.FalhaInesperada, MensagensErro.FalhaInesperada);
    }

    [Fact]
    public void Mensagens_nao_usam_jargao_tecnico()
    {
        foreach (var (nome, valor) in Todas)
        {
            foreach (var termo in JargaoProibido)
            {
                Assert.DoesNotContain(termo, valor, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Mensagens_nao_culpabilizam_o_usuario()
    {
        foreach (var (nome, valor) in Todas)
        {
            foreach (var termo in CulpaProibida)
            {
                Assert.DoesNotContain(termo, valor, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Erro_de_movimentacao_sempre_cita_a_quarentena_como_destino_seguro()
    {
        // §18/§37: mesmo no erro, o usuário fica sabendo que nada é descartado.
        Assert.Contains("quarentena", MensagensErro.MovimentacaoNaoConcluida,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intactos", MensagensErro.FalhaInesperada,
            StringComparison.OrdinalIgnoreCase);
    }
}
