using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T07 — PlaceholderPolicy (SPEC §6): OFFLINE, RECALL_ON_OPEN, RECALL_ON_DATA_ACCESS
/// e ReparsePoint marcam a entrada como placeholder. Policy pura: em Linux os atributos
/// são simulados construindo FileEntry diretamente (não há FS nativo com esses bits aqui).
/// </summary>
public class PlaceholderPolicyTests
{
    private static FileEntry Entry(FileAttributes attributes) => new()
    {
        Path = "/root/entry",
        Size = 1,
        MtimeUtc = DateTimeOffset.UnixEpoch,
        Attributes = attributes,
        VolumeId = "v",
        FileId = "f",
    };

    [Theory]
    [InlineData(FileAttributes.Offline)]                          // 0x1000
    [InlineData((FileAttributes)0x00040000)]                      // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData((FileAttributes)0x00400000)]                      // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    [InlineData(FileAttributes.ReparsePoint)]                     // 0x400
    public void IsPlaceholder_AttributeSimulado_Detecta(FileAttributes attribute)
    {
        var entry = Entry(attribute);

        Assert.True(PlaceholderPolicy.IsPlaceholder(entry));
    }

    [Fact]
    public void IsPlaceholder_ArquivoNormal_NaoEhPlaceholder()
    {
        Assert.False(PlaceholderPolicy.IsPlaceholder(Entry(FileAttributes.Normal)));
    }

    [Fact]
    public void IsPlaceholder_CombinacaoComBitsNormais_Detecta()
    {
        // OneDrive: ReadOnly | Offline | ReparsePoint num mesmo arquivo real.
        var entry = Entry(FileAttributes.ReadOnly | FileAttributes.Offline | FileAttributes.ReparsePoint);

        Assert.True(PlaceholderPolicy.IsPlaceholder(entry));
    }

    [Theory]
    [InlineData(FileAttributes.Offline, PlaceholderKind.Offline)]
    [InlineData((FileAttributes)0x00040000, PlaceholderKind.RecallOnOpen)]
    [InlineData((FileAttributes)0x00400000, PlaceholderKind.RecallOnDataAccess)]
    [InlineData(FileAttributes.ReparsePoint, PlaceholderKind.ReparsePoint)]
    public void Classify_AttributeSimulado_ReportaKindCorreto(FileAttributes attribute, PlaceholderKind expected)
    {
        Assert.Equal(expected, PlaceholderPolicy.Classify(Entry(attribute)));
    }
}
