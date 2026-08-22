namespace Doctor.Gui;

/// <summary>
/// Mensagens de erro padrão da GUI (G2 — t_87f625aa; §37):
/// linguagem simples, sem jargão técnico e sem culpar o usuário.
/// Recurso CENTRALIZADO: qualquer tela que precise reportar falha usa estes
/// textos, nunca um literal próprio. Constantes determinísticas (sem hora
/// local, sem locale, sem aleatoriedade) e sem vocabulário destrutivo
/// (a guarda GUIVM-02 varre este arquivo junto com as telas).
/// </summary>
public static class MensagensErro
{
    /// <summary>A pasta escolhida não pôde ser lida (inexistente, sem permissão ou fora do ar).</summary>
    public const string PastaNaoDisponivel =
        "Não conseguimos acessar essa pasta agora. Confira se ela existe e tente de novo.";

    /// <summary>Nenhum item foi selecionado antes de uma ação na fila.</summary>
    public const string NenhumItemSelecionado =
        "Escolha pelo menos um item da lista antes de continuar.";

    /// <summary>A movimentação para a quarentena não terminou; nada se perdeu.</summary>
    public const string MovimentacaoNaoConcluida =
        "A movimentação para a quarentena não terminou. Seus arquivos estão intactos — tente novamente mais tarde.";

    /// <summary>Falha inesperada; estado preservado.</summary>
    public const string FalhaInesperada =
        "Algo inesperado aconteceu e paramos por segurança. Seus arquivos permanecem intactos.";
}
