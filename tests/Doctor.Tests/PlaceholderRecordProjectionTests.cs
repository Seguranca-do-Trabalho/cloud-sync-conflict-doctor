using Doctor.Core;
using Doctor.Gui.Engine;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// T09 (t_349dc2c0) — report Placeholders[] projection (schema-report-v1.md §6.3):
/// every entry marked as a placeholder, sorted by path bytes, with labels
/// without duplicates in the declared canonical order (reparse_point → recall_on_data_access →
/// recall_on_open → offline) and size obtained from metadata (never by opening).
/// </summary>
public class PlaceholderRecordProjectionTests
{
    private static FileEntry Entry(string path, long size, FileAttributes attrs)
    {
        var baseEntry = new FileEntry
        {
            Path = path,
            Size = size,
            MtimeUtc = DateTimeOffset.UnixEpoch,
            Attributes = attrs,
            VolumeId = "vol-test",
            FileId = "1",
        };

        // Marking identical to Level 0 (OrderedFileEnumerator/CrossPlatformEnumerator).
        return baseEntry with
        {
            IsPlaceholder = PlaceholderPolicy.IsPlaceholder(baseEntry),
            PlaceholderKind = PlaceholderPolicy.Classify(baseEntry),
        };
    }

    [Fact]
    public void Labels_InCanonicalDeclaredOrder_NoDuplicates()
    {
        // All four bits: schema §6.3 order, not the policy precedence order.
        var all = (FileAttributes.Offline
            | (FileAttributes)0x00040000 // RECALL_ON_OPEN
            | (FileAttributes)0x00400000 // RECALL_ON_DATA_ACCESS
            | FileAttributes.ReparsePoint);

        var records = PlaceholderReport.Records([Entry("p/x.bin", 7, all)]);

        var single = Assert.Single(records);
        Assert.Equal(["reparse_point", "recall_on_data_access", "recall_on_open", "offline"], single.Kinds);
        Assert.Equal(7, single.SizeBytes);
    }

    [Fact]
    public void Partial_Labels_EachBitWithItsLabel()
    {
        var records = PlaceholderReport.Records(
        [
            Entry("p/a.bin", 1, FileAttributes.ReparsePoint),
            Entry("p/b.bin", 2, (FileAttributes)0x00400000), // RECALL_ON_DATA_ACCESS
            Entry("p/c.bin", 3, (FileAttributes)0x00040000), // RECALL_ON_OPEN
            Entry("p/d.bin", 4, FileAttributes.Offline),
        ]);

        Assert.Equal(
            ["reparse_point"],
            records.Single(r => r.Path == "p/a.bin").Kinds);
        Assert.Equal(
            ["recall_on_data_access"],
            records.Single(r => r.Path == "p/b.bin").Kinds);
        Assert.Equal(
            ["recall_on_open"],
            records.Single(r => r.Path == "p/c.bin").Kinds);
        Assert.Equal(
            ["offline"],
            records.Single(r => r.Path == "p/d.bin").Kinds);
    }

    [Fact]
    public void List_SortedByPathBytes_PlaceholdersOnly()
    {
        var normal = Entry("t/zz.txt", 9, FileAttributes.Normal); // never enters

        var records = PlaceholderReport.Records(
        [
            Entry("t/video.mp4", 100, FileAttributes.Offline),
            normal,
            Entry("t/archive dead.docx", 50, (FileAttributes)0x00040000),
        ]);

        Assert.Equal(2, records.Count);
        // UTF-8 bytes: ' ' (0x20) < 'v' — "archive..." before "video..."
        Assert.Equal("t/archive dead.docx", records[0].Path);
        Assert.Equal("t/video.mp4", records[1].Path);
    }

    [Fact]
    public void NonPlaceholder_GeneratesNoRecord()
    {
        Assert.Empty(PlaceholderReport.Records([Entry("t/a.txt", 1, FileAttributes.Normal)]));
    }
}
