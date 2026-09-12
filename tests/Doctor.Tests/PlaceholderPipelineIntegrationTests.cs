namespace Doctor.Tests;

using System.Security.Cryptography;
using Doctor.Core;

/// <summary>
/// T09 (t_349dc2c0) — PLH-02 (docs/test-strategy.md §3.2): FX-PLACEHOLDER tree in
/// tmp with simulated placeholders via `.placeholder-meta.json` sidecar (T04 convention).
/// Complete scan; placeholder_bytes_read == 0; placeholder content intact
/// byte-for-byte (sha256 before == after); Placeholders[] consistent with enumeration.
/// </summary>
public class PlaceholderPipelineIntegrationTests : IDisposable
{
    private readonly string _root;

    public PlaceholderPipelineIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdt09-fx-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>T04 convention: JSON sidecar marks the file as a simulated placeholder
    /// with attributes that only exist on Windows. The file itself remains a regular
    /// FS file — the scan must NEVER open it.</summary>
    private static void MarkAsPlaceholder(string filePath, string reason)
    {
        var sidecar = filePath + ".placeholder-meta.json";
        File.WriteAllText(sidecar, $"{{\"kind\":\"{reason}\"}}");
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    [Fact]
    public void MixedTree_CompleteScan_ZeroPlaceholderBytes_ContentIntact()
    {
        // ---- FX-PLACEHOLDER: normals + 4 SPEC §21 reasons via sidecar + real reparse
        var docs = Path.Combine(_root, "docs");
        var dead = Path.Combine(_root, "dead file");
        _ = Directory.CreateDirectory(docs);
        _ = Directory.CreateDirectory(dead);

        var normalA = Path.Combine(docs, "normal-a.txt");
        var normalB = Path.Combine(docs, "normal-b.txt"); // identical duplicate of normal-a
        var unique = Path.Combine(_root, "unique.dat");
        var offline = Path.Combine(dead, "old report.docx");
        var roo = Path.Combine(docs, "contract.pdf");
        var roda = Path.Combine(docs, "video-lesson.mp4");

        var normalContent = "normal content shared by both copies"u8.ToArray();
        File.WriteAllBytes(normalA, normalContent);
        File.WriteAllBytes(normalB, normalContent);
        File.WriteAllBytes(unique, "file without pair"u8.ToArray());

        var offlineBytes = new byte[96 * 1024]; // >0 so test proves zero reads
        RandomNumberGenerator.Fill(offlineBytes);
        File.WriteAllBytes(offline, offlineBytes);
        File.WriteAllBytes(roo, new byte[32 * 1024]);
        File.WriteAllBytes(roda, new byte[160 * 1024]);
        MarkAsPlaceholder(offline, "offline");
        MarkAsPlaceholder(roo, "recall_on_open");
        MarkAsPlaceholder(roda, "recall_on_data_access");

        // REAL reparse (file symlink) — marked by FS, no sidecar.
        File.CreateSymbolicLink(Path.Combine(_root, "shortcut.txt"), normalA);

        // Hashes BEFORE scan (proof of no byte-for-byte destruction).
        var offlineHashBefore = Sha256(offline);
        var rooHashBefore = Sha256(roo);
        var rodaHashBefore = Sha256(roda);

        // ---- SCAN (enumerator pipeline: physical → ordered → T04 convention)
        var result = new SidecarPlaceholderEnumerator(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()))
            .Enumerate(_root, CancellationToken.None);

        var files = result.Files;
        var byPath = files.ToDictionary(f => f.Path, f => f);

        // Sidecar marks the three; symlink is marked by origin.
        Assert.True(byPath[offline].IsPlaceholder);
        Assert.True(byPath[roo].IsPlaceholder);
        Assert.True(byPath[roda].IsPlaceholder);
        Assert.True(byPath[Path.Combine(_root, "shortcut.txt")].IsPlaceholder);

        // Normals remain normal.
        Assert.False(byPath[normalA].IsPlaceholder);
        Assert.False(byPath[unique].IsPlaceholder);

        // ---- Placeholder content intact byte-for-byte
        Assert.Equal(offlineHashBefore, Sha256(offline));
        Assert.Equal(rooHashBefore, Sha256(roo));
        Assert.Equal(rodaHashBefore, Sha256(roda));

        // ---- Telemetry: count and absolute invariant (SPEC §21)
        Assert.Equal(files.Count, result.Telemetry.FilesEnumerated);
        Assert.Equal(4, result.Telemetry.FilesPlaceholder);
        Assert.Equal(0, result.Telemetry.PlaceholderBytesRead);

        // ---- Placeholders[] per schema v1 §6.3 (order by path bytes)
        var records = PlaceholderReport.Records(files);
        Assert.Equal(4, records.Count);
        Assert.Equal(
            records.Select(r => r.Path).ToArray(),
            records.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal("reparse_point", records.Single(r => r.Path.EndsWith("shortcut.txt", StringComparison.Ordinal)).Kinds.Single());
        Assert.Equal("offline", records.Single(r => r.Path == offline).Kinds.Single());
        Assert.Equal("recall_on_open", records.Single(r => r.Path == roo).Kinds.Single());
        Assert.Equal("recall_on_data_access", records.Single(r => r.Path == roda).Kinds.Single());

        // ---- Gate on hasher: no placeholder reaches content (PLH-01 in practice).
        // Any call on placeholder explodes; normals hash normally.
        var source = new CountingStreamSource();
        foreach (var f in files.Where(f => !f.IsPlaceholder))
        {
            source.Register(f.Path, File.ReadAllBytes(f.Path));
        }

        var hasher = new PlaceholderGuardedHasher(CountingHasher.Using(source));

        foreach (var placeholder in files.Where(f => f.IsPlaceholder))
        {
            Assert.Throws<PlaceholderReadException>(() => hasher.PartialHash(placeholder));
            Assert.Throws<PlaceholderReadException>(() => hasher.FullHash(placeholder));
        }

        _ = hasher.PartialHash(byPath[normalA]); // normals pass through gate

        // No stream was opened on any placeholder (double PLH-01 evidence).
        foreach (var placeholder in files.Where(f => f.IsPlaceholder))
        {
            Assert.Equal(0, source.OpenCount(placeholder.Path));
            Assert.Equal(0, source.BytesRead(placeholder.Path));
        }
    }

    [Fact]
    public void ScanOfTreeWithOnlyPlaceholders_ReadsNothing_ListsAll()
    {
        var p1 = Path.Combine(_root, "only-offline.bin");
        var p2 = Path.Combine(_root, "only-roda.bin");
        File.WriteAllBytes(p1, new byte[4096]);
        File.WriteAllBytes(p2, new byte[8192]);
        MarkAsPlaceholder(p1, "offline");
        MarkAsPlaceholder(p2, "recall_on_data_access");

        var result = new SidecarPlaceholderEnumerator(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()))
            .Enumerate(_root, CancellationToken.None);

        Assert.Equal(2, result.Telemetry.FilesEnumerated); // sidecars don't count
        Assert.Equal(2, result.Telemetry.FilesPlaceholder);
        Assert.Equal(0, result.Telemetry.FilesSkipped);
        Assert.Equal(0, result.Telemetry.FilesPartialHashed);
        Assert.Equal(0, result.Telemetry.PlaceholderBytesRead);
        Assert.Equal(2, PlaceholderReport.Records(result.Files).Count);
    }
}
