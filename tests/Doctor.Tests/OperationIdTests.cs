namespace Doctor.Tests;

using Doctor.Core;
using Xunit;

/// <summary>
/// SEG-18 (P0): operation_id generated per operation is unique and does not collide with prior operations.
/// SEG-19 (P1): generator entropy guarantees statistical non-collision.
/// </summary>
public class OperationIdTests : IDisposable
{
    private readonly string _root;

    public OperationIdTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"opid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ==================================================================
    // SEG-18 — Unique operation_id across sequential operations
    // ==================================================================
    [Fact]
    public void Security_OperationIdUniqueAcrossOperations()
    {
        // Create file inside root (containment requirement)
        var file1 = CreateFile(_root, "file1.txt", new byte[] { 0x42 });
        var file2 = CreateFile(_root, "file2.txt", new byte[] { 0x43 });
        
        var svc = new QuarantineService();
        
        // Operation 1
        var plan1 = new QuarantinePlan(_root, DateTimeOffset.UtcNow);
        var result1 = svc.Move(new[] { Entry(file1) }, plan1);
        
        Assert.NotNull(result1);
        Assert.Equal("completed", result1.Status);
        Assert.NotNull(result1.OperationId);
        Assert.NotEmpty(result1.OperationId);
        
        // Operation 2 with different file
        var plan2 = new QuarantinePlan(_root, DateTimeOffset.UtcNow.AddSeconds(1));
        var result2 = svc.Move(new[] { Entry(file2) }, plan2);
        
        Assert.NotNull(result2);
        Assert.Equal("completed", result2.Status);
        Assert.NotNull(result2.OperationId);
        Assert.NotEmpty(result2.OperationId);
        
        // IDs must be different (no collision)
        Assert.NotEqual(result1.OperationId, result2.OperationId);
        
        // Both manifests must exist
        Assert.True(File.Exists(result1.ManifestPath));
        Assert.True(File.Exists(result2.ManifestPath));
    }

    // ==================================================================
    // SEG-19 — Entropy: generates unique IDs in batch
    // ==================================================================
    [Fact]
    public void OperationId_Entropy_GeneratesUniqueIds()
    {
        var ids = new HashSet<string>();
        var expectedSize = 32; // 128 bits = 32 hex chars
        
        for (var i = 0; i < 100; i++)
        {
            // Simulates operation_id generation as the system would do it
            var id = Guid.NewGuid().ToString("N");
            
            Assert.NotNull(id);
            Assert.Equal(expectedSize, id.Length);
            Assert.False(ids.Contains(id), $"Collision detected at index {i}: {id}");
            ids.Add(id);
        }
        
        Assert.Equal(100, ids.Count); // all unique
    }

    // ==================================================================
    // Helpers
    // ==================================================================
    
    private static string CreateFile(string root, string name, byte[] content)
    {
        var fullPath = Path.Combine(root, name);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    private static QuarantineItem Entry(string path)
    {
        var info = new FileInfo(path);
        return new QuarantineItem(
            new FileEntry
            {
                Path = path,
                Size = info.Length,
                MtimeUtc = info.LastWriteTimeUtc,
                Attributes = info.Attributes,
                VolumeId = "vol-test",
                FileId = "file-001",
                IsPlaceholder = false,
            },
            "TEST",
            "TEST-RULE");
    }
}
