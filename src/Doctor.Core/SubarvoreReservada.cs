namespace Doctor.Core;

/// <summary>
/// Nome reservado pela ferramenta na raiz escaneada (ADR-0002; ADR-0010 §1):
/// <c>&lt;raiz&gt;/ConflictDoctor/</c> — quarentena §18/SPEC e metadados de operação.
/// Fonte única do literal: a quarentena publica sob este nome e a enumeração Level 0
/// exclui esta subárvore da varredura por prefixo em bytes do caminho canônico
/// (SEG-12, adendo T-15 do audit GATE 5; caso T-06 do threat-model, regra R1).
/// Alterar o valor aqui muda os dois lados do contrato simultaneamente.
/// </summary>
public static class SubarvoreReservada
{
    /// <summary>Nome do diretório reservado na raiz escaneada.</summary>
    public const string NomeDiretorio = "ConflictDoctor";
}
