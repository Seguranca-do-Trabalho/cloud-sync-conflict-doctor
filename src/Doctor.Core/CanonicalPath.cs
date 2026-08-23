namespace Doctor.Core;

/// <summary>
/// Canon de caminhos relativos e defesa contra nomes hostis (card T17 — sprint S11;
/// SPEC §35 agente Security "path traversal"; GATE 5 "path/reparse attacks testados";
/// filosofia de falha fechada do ADR-0002).
///
/// Contrato de <see cref="IsValidName"/>: aprova SOMENTE nome relativo seguro —
/// rejeita null/vazio, caracteres de controle (&lt; 0x20 e DEL), segmento vazio
/// (sequências "//", separador inicial/final em qualquer variante), qualquer
/// segmento "." ou ".." (traversal nunca é sanitizado silenciosamente), nomes
/// reservados NTFS (CON, PRN, AUX, NUL, COM1-9, LPT1-9 — reconhecidos pelo Windows
/// como STEM antes do primeiro ponto, em qualquer caixa, em qualquer segmento do
/// caminho), terminação em ponto ou espaço (o Win32 descarta esses sufixos na
/// criação — vetor de confusão) e extensão acima de 255 caracteres (componente que
/// o NTFS não armazenaria não pode receber aval do gate).
///
/// Contrato de <see cref="Normalize"/>: canoniza separador '\' → '/', preserva a
/// caixa original (a comparação canônica fica por conta de
/// <see cref="StringComparer.OrdinalIgnoreCase"/> no consumidor — semântica de
/// igualdade NTFS; NÃO confundir com <see cref="PathOrder"/>, que ordena o
/// relatório por Ordinal sensível a caixa conforme SPEC §3) e trunca extensão
/// acima de 255 para 255 — única reparação permitida. Qualquer outro nome hostil
/// lança <see cref="ArgumentException"/>: falha fechada, nunca saída sanitizada.
///
/// O caminho canônico é RELATIVO à raiz do scan: caminho absoluto ("/x", "C:\x")
/// produz segmento vazio/drive e é rejeitado — a fronteira da árvore é responsabilidade
/// da enumeração (ADR-0004), e este tipo garante que nada aprovado escape dela.
/// </summary>
public static class CanonicalPath
{
    /// <summary>Tabela reservada Win32/NTFS: nome exato (antes do primeiro ponto) proibido em qualquer segmento.</summary>
    private static readonly HashSet<string> NomesReservados = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Teto da extensão (cartão T17): acima disso o componente é hostil até reparo.</summary>
    public const int ComprimentoMaximoExtensao = 255;

    /// <summary>
    /// Gate de nome: true somente se <paramref name="path"/> é um caminho relativo
    /// seguro pelos critérios da documentação do tipo. Falha fechada — na dúvida,
    /// false.
    /// </summary>
    public static bool IsValidName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c < ' ' || c == '\u007F')
            {
                return false; // caractere de controle (inclui DEL)
            }
        }

        var segmentos = path.Split('/', '\\');

        foreach (var segmento in segmentos)
        {
            // Segmento vazio: sequência "//", separador nas pontas ou caminho absoluto.
            // "." e "..": traversal em qualquer posição — nunca aprovado.
            if (segmento.Length == 0 || segmento == "." || segmento == "..")
            {
                return false;
            }

            // Win32 descarta ponto/espaço finais ao criar o componente; aprovar
            // seria endossar um nome que o volume reescreve.
            if (segmento.EndsWith('.') || segmento.EndsWith(' '))
            {
                return false;
            }

            // Reserva NTFS vale para o STEM (porção antes do primeiro ponto),
            // em qualquer caixa — "con.txt" colide com o device CON tanto quanto "CON".
            var ponto = segmento.IndexOf('.');
            var stem = ponto < 0 ? segmento : segmento[..ponto];

            if (NomesReservados.Contains(stem))
            {
                return false;
            }
        }

        // Extensão do ÚLTIMO segmento: componente com mais de 255 caracteres de
        // extensão não existe no NTFS; o gate não aprova o impossível (a rota
        // legítima é o reparo por <see cref="Normalize"/>).
        var ultimo = segmentos[^1];
        var ultimoPonto = ultimo.LastIndexOf('.');

        if (ultimoPonto >= 0 && ultimo.Length - ultimoPonto - 1 > ComprimentoMaximoExtensao)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Forma canônica do caminho: separador '/', caixa original preservada e
    /// extensão truncada a 255 quando excede o teto. Rejeita com
    /// <see cref="ArgumentException"/> todo nome hostil que não seja o caso de
    /// extensão longa — nunca devolve forma sanitizada de traversal ou reservado.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Caminho nulo ou vazio não tem forma canônica.", nameof(path));
        }

        var canonico = path.Replace('\\', '/');

        if (IsValidName(canonico))
        {
            return canonico;
        }

        // Único reparo permitido: extensão acima de 255 é truncada ao teto.
        var reparado = TruncarExtensao(canonico);

        if (!ReferenceEquals(reparado, canonico) && IsValidName(reparado))
        {
            return reparado;
        }

        throw new ArgumentException(
            $"Caminho hostil recusado pelo canon (traversal, reservado NTFS, controle ou estrutura inválida): \"{path}\".",
            nameof(path));
    }

    /// <summary>
    /// Retorna o caminho com a extensão do último segmento truncada a 255
    /// caracteres, ou a MESMA instância quando não há reparo a fazer (evita
    /// alocação no caminho comum e permite ao chamador distinguir os casos).
    /// </summary>
    private static string TruncarExtensao(string caminho)
    {
        var ultimoSeparador = caminho.LastIndexOf('/');
        var ultimoPonto = caminho.LastIndexOf('.');
        var inicioExtensao = ultimoPonto + 1;

        // Ponto precisa estar no último segmento e abrir uma extensão longa demais.
        if (ultimoPonto < 0 || ultimoPonto < ultimoSeparador || inicioExtensao <= ultimoSeparador)
        {
            return caminho;
        }

        var comprimentoExtensao = caminho.Length - inicioExtensao;

        if (comprimentoExtensao <= ComprimentoMaximoExtensao)
        {
            return caminho;
        }

        return caminho.Remove(inicioExtensao, comprimentoExtensao - ComprimentoMaximoExtensao);
    }
}
