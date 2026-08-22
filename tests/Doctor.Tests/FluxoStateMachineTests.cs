using Doctor.Gui.Flow;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G3 (t_8d08c08b) — máquina de estados do fluxo §15, pura (sem Avalonia).
/// Ciclo 1 (RED): os 9 estados são as 9 telas; só existe a ordem válida;
/// qualquer gatilho fora da ordem lança TransicaoInvalidaException e o estado
/// corrente NÃO muda (fail-fast: silenciar esconderia ligação errada de botão).
/// </summary>
public class FluxoStateMachineTests
{
    private static FlowStateMachine NovaMaquina() => new();

    // ------------------------------------------------------------------
    // Caminho feliz completo: a única sequência válida do fluxo §15.
    // ------------------------------------------------------------------
    [Fact]
    public void Caminho_valido_percorre_as_nove_telas_na_ordem_do_fluxo()
    {
        var m = NovaMaquina();
        var ordem = new List<FlowScreen> { m.Current };

        m.Fire(FlowTrigger.StartScan);
        ordem.Add(m.Current);

        m.HasReport = true;                 // relatório completo chegou
        m.Fire(FlowTrigger.ScanCompleted);
        ordem.Add(m.Current);

        m.Fire(FlowTrigger.OpenDuplicates);
        ordem.Add(m.Current);

        m.Fire(FlowTrigger.Back);
        ordem.Add(m.Current);

        m.Fire(FlowTrigger.OpenConflicts);
        ordem.Add(m.Current);

        m.HasSelectedConflict = true;       // grupo escolhido na tela Conflitos
        m.Fire(FlowTrigger.CompareConflict);
        ordem.Add(m.Current);

        m.HasQueuedItems = true;            // versões não mantidas enfileiradas
        m.Fire(FlowTrigger.QueueOtherVersions);
        ordem.Add(m.Current);

        m.Fire(FlowTrigger.ConfirmQuarantine);
        ordem.Add(m.Current);

        m.HasOperationId = true;            // operation_id fake atribuído (§18)
        m.Fire(FlowTrigger.OpenConfirmation);
        ordem.Add(m.Current);

        var esperado = new[]
        {
            FlowScreen.ChooseFolder, FlowScreen.Scanning, FlowScreen.Summary,
            FlowScreen.Duplicates, FlowScreen.Summary, FlowScreen.Conflicts,
            FlowScreen.Compare, FlowScreen.ChooseAction, FlowScreen.Quarantine,
            FlowScreen.Confirmation,
        };

        Assert.Equal(esperado, ordem);
    }

    // ------------------------------------------------------------------
    // Matriz parametrizada de TODAS as arestas válidas do grafo.
    // (origem, gatilho, destino, pré-condições a ligar)
    // ------------------------------------------------------------------
    public static TheoryData<FlowScreen, FlowTrigger, FlowScreen, string> ArestasValidas() => new()
    {
        // ChooseFolder → Scanning
        { FlowScreen.ChooseFolder, FlowTrigger.StartScan, FlowScreen.Scanning, "" },
        // Scanning → Summary (só com relatório completo)
        { FlowScreen.Scanning, FlowTrigger.ScanCompleted, FlowScreen.Summary, "report" },
        // Resumo abre Duplicatas ou Conflitos (com relatório)
        { FlowScreen.Summary, FlowTrigger.OpenDuplicates, FlowScreen.Duplicates, "report" },
        { FlowScreen.Summary, FlowTrigger.OpenConflicts, FlowScreen.Conflicts, "report" },
        // Duplicatas → Conflitos (Continuar) ou volta ao Resumo
        { FlowScreen.Duplicates, FlowTrigger.OpenConflicts, FlowScreen.Conflicts, "report" },
        { FlowScreen.Duplicates, FlowTrigger.Back, FlowScreen.Summary, "report" },
        // Conflitos → Comparar (com seleção) ou volta às Duplicatas
        { FlowScreen.Conflicts, FlowTrigger.CompareConflict, FlowScreen.Compare, "report,selecao" },
        { FlowScreen.Conflicts, FlowTrigger.Back, FlowScreen.Duplicates, "report" },
        // Comparar → Escolher ação (com fila) ou volta aos Conflitos
        { FlowScreen.Compare, FlowTrigger.QueueOtherVersions, FlowScreen.ChooseAction, "report,selecao,fila" },
        { FlowScreen.Compare, FlowTrigger.Back, FlowScreen.Conflicts, "report,selecao" },
        // Escolher ação → Quarentena (fila não vazia) ou Repensar → Comparar
        { FlowScreen.ChooseAction, FlowTrigger.ConfirmQuarantine, FlowScreen.Quarantine, "report,selecao,fila" },
        { FlowScreen.ChooseAction, FlowTrigger.Back, FlowScreen.Compare, "report,selecao,fila" },
        // Quarentena → Confirmação (com operation_id) ou volta a Escolher ação
        { FlowScreen.Quarantine, FlowTrigger.OpenConfirmation, FlowScreen.Confirmation, "report,selecao,fila,opid" },
        { FlowScreen.Quarantine, FlowTrigger.Back, FlowScreen.ChooseAction, "report,selecao,fila" },
        // Confirmação → novo exame
        { FlowScreen.Confirmation, FlowTrigger.Restart, FlowScreen.ChooseFolder, "report,selecao,fila,opid" },
    };

    [Theory]
    [MemberData(nameof(ArestasValidas))]
    public void Aresta_valida_leva_ao_destino_exato(
        FlowScreen origem, FlowTrigger gatilho, FlowScreen destino, string preCondicoes)
    {
        var m = MaquinaEm(origem, preCondicoes);

        m.Fire(gatilho);

        Assert.Equal(destino, m.Current);
    }

    // ------------------------------------------------------------------
    // Transição inválida: lança e mantém o estado (nenhuma sequência de
    // cliques leva a um estado fora da ordem válida).
    // ------------------------------------------------------------------
    public static TheoryData<FlowScreen, FlowTrigger, string> ArestasInvalidas() => new()
    {
        // Atalhos proibidos a partir do início
        { FlowScreen.ChooseFolder, FlowTrigger.ScanCompleted, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.OpenDuplicates, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.OpenConflicts, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.CompareConflict, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.ConfirmQuarantine, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.OpenConfirmation, "" },
        { FlowScreen.ChooseFolder, FlowTrigger.Back, "" },
        // Escaneando: nada sai sem relatório completo (nem voltar, nem reiniciar)
        { FlowScreen.Scanning, FlowTrigger.Back, "" },
        { FlowScreen.Scanning, FlowTrigger.Restart, "" },
        { FlowScreen.Scanning, FlowTrigger.OpenDuplicates, "" },
        { FlowScreen.Scanning, FlowTrigger.ScanCompleted, "" }, // sem pré-condição "report"
        // Resumo: sem relatório ninguém entra em Duplicatas/Conflitos; sem atalhos
        { FlowScreen.Summary, FlowTrigger.OpenDuplicates, "" },
        { FlowScreen.Summary, FlowTrigger.OpenConflicts, "" },
        { FlowScreen.Summary, FlowTrigger.ConfirmQuarantine, "" },
        { FlowScreen.Summary, FlowTrigger.Back, "" },
        { FlowScreen.Summary, FlowTrigger.StartScan, "" },
        // Duplicatas: sem relatório não entra nem avança; sem atalhos de fila
        { FlowScreen.Duplicates, FlowTrigger.ConfirmQuarantine, "report" },
        { FlowScreen.Duplicates, FlowTrigger.QueueOtherVersions, "report" },
        { FlowScreen.Duplicates, FlowTrigger.OpenDuplicates, "report" },
        // Conflitos: sem seleção não compara; sem relatório não entra em conflitos de novo
        { FlowScreen.Conflicts, FlowTrigger.CompareConflict, "report" },
        { FlowScreen.Conflicts, FlowTrigger.ConfirmQuarantine, "report,selecao" },
        // Comparar: sem seleção não enfileira; sem fila não confirma
        { FlowScreen.Compare, FlowTrigger.QueueOtherVersions, "report,selecao" },
        { FlowScreen.Compare, FlowTrigger.ConfirmQuarantine, "report,selecao" },
        // Escolher ação: fila vazia não entra em Quarentena
        { FlowScreen.ChooseAction, FlowTrigger.ConfirmQuarantine, "report,selecao" },
        { FlowScreen.ChooseAction, FlowTrigger.OpenConfirmation, "report,selecao,fila" },
        // Quarentena: sem operation_id não vê a Confirmação
        { FlowScreen.Quarantine, FlowTrigger.OpenConfirmation, "report,selecao,fila" },
        { FlowScreen.Quarantine, FlowTrigger.QueueOtherVersions, "report,selecao,fila" },
        // Confirmação: operação registrada não volta atrás nem reenfileira
        { FlowScreen.Confirmation, FlowTrigger.Back, "report,selecao,fila,opid" },
        { FlowScreen.Confirmation, FlowTrigger.ConfirmQuarantine, "report,selecao,fila,opid" },
        { FlowScreen.Confirmation, FlowTrigger.OpenDuplicates, "report,selecao,fila,opid" },
        { FlowScreen.Confirmation, FlowTrigger.StartScan, "report,selecao,fila,opid" },
    };

    [Theory]
    [MemberData(nameof(ArestasInvalidas))]
    public void Transicao_invalida_lanca_e_mantem_o_estado(
        FlowScreen origem, FlowTrigger gatilho, string preCondicoes)
    {
        var m = MaquinaEm(origem, preCondicoes);

        var excecao = Assert.Throws<TransicaoInvalidaException>(() => m.Fire(gatilho));

        Assert.Equal(origem, m.Current);
        Assert.Contains(gatilho.ToString(), excecao.Message, StringComparison.Ordinal);
        Assert.Contains(origem.ToString(), excecao.Message, StringComparison.Ordinal);
    }

    /// <summary>CanFire espelha Fire: verdadeiro só onde Fire não lançaria.</summary>
    [Theory]
    [MemberData(nameof(ArestasValidas))]
    public void CanFire_espelha_as_arestas_validas(
        FlowScreen origem, FlowTrigger gatilho, FlowScreen _, string preCondicoes)
    {
        var m = MaquinaEm(origem, preCondicoes);

        Assert.True(m.CanFire(gatilho));
    }

    [Theory]
    [MemberData(nameof(ArestasInvalidas))]
    public void CanFire_nega_toda_aresta_invalida(
        FlowScreen origem, FlowTrigger gatilho, string preCondicoes)
    {
        var m = MaquinaEm(origem, preCondicoes);

        Assert.False(m.CanFire(gatilho));
    }

    // ------------------------------------------------------------------
    // Auxiliar: posiciona a máquina num estado com pré-condições ligadas.
    // As pré-condições são sempre alcançadas pelo caminho válido a partir
    // de ChooseFolder — nunca injetadas por reflexo — exceto os flags de
    // guarda, que representam dados do mundo (relatório, seleção, fila, id).
    // ------------------------------------------------------------------
    private static FlowStateMachine MaquinaEm(FlowScreen origem, string preCondicoes)
    {
        var m = NovaMaquina();
        var quer = preCondicoes.Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (origem == FlowScreen.ChooseFolder)
        {
            return m;                           // posição inicial: nada atravessado
        }

        // 1) Caminho válido até a origem. Os flags de guarda ligados ao longo do
        //    trajeto existem só para ATRAVESSAR cada transição intermediária —
        //    são normalizados no fim, nunca vazam para o gatilho sob teste.
        m.Fire(FlowTrigger.StartScan);                        // → Scanning

        if (origem != FlowScreen.Scanning)
        {
            m.HasReport = true;
            m.Fire(FlowTrigger.ScanCompleted);                // → Summary

            if (origem != FlowScreen.Summary)
            {
                m.Fire(FlowTrigger.OpenDuplicates);           // → Duplicates

                if (origem != FlowScreen.Duplicates)
                {
                    m.Fire(FlowTrigger.OpenConflicts);        // → Conflicts

                    if (origem != FlowScreen.Conflicts)
                    {
                        m.HasSelectedConflict = true;
                        m.Fire(FlowTrigger.CompareConflict);  // → Compare

                        if (origem != FlowScreen.Compare)
                        {
                            m.HasQueuedItems = true;
                            m.Fire(FlowTrigger.QueueOtherVersions); // → ChooseAction

                            if (origem != FlowScreen.ChooseAction)
                            {
                                m.Fire(FlowTrigger.ConfirmQuarantine); // → Quarantine

                                if (origem != FlowScreen.Quarantine)
                                {
                                    m.HasOperationId = true;
                                    m.Fire(FlowTrigger.OpenConfirmation); // → Confirmation
                                }
                            }
                        }
                    }
                }
            }
        }

        // 2) Normaliza os dados do mundo para EXATAMENTE as pré-condições pedidas:
        //    o cenário descreve o estado visível no momento do clique sob teste,
        //    nada mais. É isto que faz cada guarda negativa falhar pelo motivo
        //    certo (flag ausente), não por posição errada da máquina.
        m.HasReport = quer.Contains("report");
        m.HasSelectedConflict = quer.Contains("selecao");
        m.HasQueuedItems = quer.Contains("fila");
        m.HasOperationId = quer.Contains("opid");

        return m;
    }
}
