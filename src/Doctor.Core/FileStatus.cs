namespace Doctor.Core;

/// <summary>
/// Status de estabilidade de um arquivo durante o scan (threat-model T-05, regra R4).
/// Arquivos instáveis nunca entram em decisões de igualdade/divergência e nunca são
/// gravados no cache — fail-closed: dúvida => divergência potencial.
/// </summary>
public enum FileStatus
{
    /// <summary>Metadados conferidos antes e depois da leitura — estável.</summary>
    Stable,

    /// <summary>Metadados divergiram entre snapshot pré-leitura e pós-leitura.
    /// O arquivo foi marcado como instável: excluído de decisões de igualdade
    /// e nunca entra no cache (T-05/R4).</summary>
    Unstable,
}
