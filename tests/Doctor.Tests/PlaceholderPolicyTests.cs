using Doctor.Core;

namespace Doctor.Tests;

/// <summary>
/// T07 — PlaceholderPolicy (SPEC §6): OFFLINE, RECALL_ON_OPEN, RECALL_ON_DATA_ACCESS
/// and ReparsePoint mark entry as placeholder. Pure policy: on Linux attributes
/// are simulated by constructing FileEntry directly (no native FS with these bits here).
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
    public void IsPlaceholder_SimulatedAttribute_Detects(FileAttributes attribute)
    {
        var entry = Entry(attribute);

        Assert.True(PlaceholderPolicy.IsPlaceholder(entry));
    }

    [Fact]
    public void IsPlaceholder_NormalFile_IsNotPlaceholder()
    {
        Assert.False(PlaceholderPolicy.IsPlaceholder(Entry(FileAttributes.Normal)));
    }

    [Fact]
    public void IsPlaceholder_CombinationWithNormalBits_Detects()
    {
        // OneDrive: ReadOnly | Offline | ReparsePoint on the same real file.
        var entry = Entry(FileAttributes.ReadOnly | FileAttributes.Offline | FileAttributes.ReparsePoint);

        Assert.True(PlaceholderPolicy.IsPlaceholder(entry));
    }

    [Theory]
    [InlineData(FileAttributes.Offline, PlaceholderKind.Offline)]
    [InlineData((FileAttributes)0x00040000, PlaceholderKind.RecallOnOpen)]
    [InlineData((FileAttributes)0x00400000, PlaceholderKind.RecallOnDataAccess)]
    [InlineData(FileAttributes.ReparsePoint, PlaceholderKind.ReparsePoint)]
    public void Classify_SimulatedAttribute_ReportsCorrectKind(FileAttributes attribute, PlaceholderKind expected)
    {
        Assert.Equal(expected, PlaceholderPolicy.Classify(Entry(attribute)));
    }
}
