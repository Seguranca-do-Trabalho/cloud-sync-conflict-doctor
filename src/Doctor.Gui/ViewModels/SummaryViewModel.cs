using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Doctor.Gui.Engine;

namespace Doctor.Gui.ViewModels;

/// <summary>
/// Tela Resumo (§15): responde as 5 perguntas a partir do relatório em forma
/// schema v1. Contagens em dígitos crus (InvariantCulture); espaço com
/// separador decimal vírgula fixo, independente do locale da máquina (§3).
/// </summary>
public partial class SummaryViewModel : ObservableObject
{
    [ObservableProperty]
    private ScanReport? _report;

    // --- As 5 perguntas do §15, derivadas exclusivamente dos dados do relatório ---

    /// <summary>1. Arquivos encontrados = files_enumerated.</summary>
    public string FilesFound =>
        (Report?.Telemetry.FilesEnumerated ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>2. Duplicatas idênticas = cópias redundantes de cada classe.</summary>
    public string IdenticalDuplicates =>
        (Report?.IdenticalDuplicateCount ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>3. Divergências reais = grupos com divergência real de conteúdo.</summary>
    public string RealConflicts =>
        (Report?.RealConflicts.Count ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>4. Placeholders ignorados = files_placeholder (nunca abertos).</summary>
    public string PlaceholdersIgnored =>
        (Report?.Telemetry.FilesPlaceholder ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 5. Espaço recuperável com segurança: soma dos sizes dos itens elegíveis para
    /// quarentena — perdedoras das duplicatas idênticas + versões que não serão
    /// mantidas nos conflitos reais (fórmula testada explicitamente).
    /// </summary>
    public string RecoverableSpace => FormatBytes(Report?.RecoverableBytes ?? 0);

    /// <summary>Unidades legíveis; separador decimal vírgula fixo (pt-BR), nunca o locale da máquina.</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => ComVirgula(bytes / 1_073_741_824.0) + " GB",
        >= 1_048_576 => ComVirgula(bytes / 1_048_576.0) + " MB",
        >= 1_024 => ComVirgula(bytes / 1_024.0) + " KB",
        _ => $"{bytes} B",
    };

    private static string ComVirgula(double valor) =>
        valor.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');
}
