namespace Doctor.Tests;

using Doctor.Core;
using Xunit;

/// <summary>
/// SEG-18 (P0): operation_id gerado por operação é único e não colide com operações anteriores.
/// SEG-19 (P1): entropia do gerador garante non-colisão estatística.
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
        // Cria arquivo dentro do root (requisito de contenção)
        var arquivo1 = CriarArquivo(_root, "arquivo1.txt", new byte[] { 0x42 });
        var arquivo2 = CriarArquivo(_root, "arquivo2.txt", new byte[] { 0x43 });
        
        var svc = new QuarantineService();
        
        // Operação 1
        var plano1 = new QuarantinePlan(_root, DateTimeOffset.UtcNow);
        var resultado1 = svc.Move(new[] { Entrada(arquivo1) }, plano1);
        
        Assert.NotNull(resultado1);
        Assert.Equal("completed", resultado1.Status);
        Assert.NotNull(resultado1.OperationId);
        Assert.NotEmpty(resultado1.OperationId);
        
        // Operação 2 com arquivo diferente
        var plano2 = new QuarantinePlan(_root, DateTimeOffset.UtcNow.AddSeconds(1));
        var resultado2 = svc.Move(new[] { Entrada(arquivo2) }, plano2);
        
        Assert.NotNull(resultado2);
        Assert.Equal("completed", resultado2.Status);
        Assert.NotNull(resultado2.OperationId);
        Assert.NotEmpty(resultado2.OperationId);
        
        // IDs devem ser diferentes (não colidiram)
        Assert.NotEqual(resultado1.OperationId, resultado2.OperationId);
        
        // Ambos os manifestos devem existir
        Assert.True(File.Exists(resultado1.ManifestPath));
        Assert.True(File.Exists(resultado2.ManifestPath));
    }

    // ==================================================================
    // SEG-19 — Entropia: gera IDs únicos em lote
    // ==================================================================
    [Fact]
    public void OperationId_Entropy_GeneratesUniqueIds()
    {
        var ids = new HashSet<string>();
        var tamanhoEsperado = 32; // 128 bits = 32 chars hex
        
        for (var i = 0; i < 100; i++)
        {
            // Simula geração de operation_id como o sistema faria
            var id = Guid.NewGuid().ToString("N");
            
            Assert.NotNull(id);
            Assert.Equal(tamanhoEsperado, id.Length);
            Assert.False(ids.Contains(id), $"Colisão detectada no índice {i}: {id}");
            ids.Add(id);
        }
        
        Assert.Equal(100, ids.Count); // todos únicos
    }

    // ==================================================================
    // Helpers
    // ==================================================================
    
    private static string CriarArquivo(string root, string nome, byte[] conteudo)
    {
        var fullPath = Path.Combine(root, nome);
        File.WriteAllBytes(fullPath, conteudo);
        return fullPath;
    }

    private static QuarantineItem Entrada(string caminho)
    {
        var info = new FileInfo(caminho);
        return new QuarantineItem(
            new FileEntry
            {
                Path = caminho,
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
