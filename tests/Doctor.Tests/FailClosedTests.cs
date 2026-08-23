using Doctor.Core;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// S11-5 — Fail-closed (T-11): sem hash completo verificado, sem decisão.
/// Falha de leitura/hash em L3 recusa a classificação do GRUPO INTEIRO:
/// nada vira duplicata nem conflito; o grupo emerge como UnresolvedGroup
/// auditável e nunca é elegível a resolução/quarentena.
/// </summary>
public sealed class FailClosedTests
{
    private static FileEntry Entrada(string path, long size, string fileId) => new()
    {
        Path = path,
        Size = size,
        MtimeUtc = DateTimeOffset.UnixEpoch,
        Attributes = FileAttributes.Normal,
        VolumeId = "vol-fc",
        FileId = fileId,
        IsPlaceholder = false,
        PlaceholderKind = null,
    };

    /// <summary>Hasher que falha a leitura nos caminhos informados (simula IO error).</summary>
    private sealed class FlakyHasher(params string[] failingPaths) : IHasher
    {
        public string PartialHash(FileEntry entry, CancellationToken ct) =>
            Failing(entry) ? throw new IOException("simulado: disco sumiu") : "p-" + entry.Path;

        public string FullHash(FileEntry entry, CancellationToken ct) =>
            Failing(entry) ? throw new IOException("simulado: disco sumiu") : "f-" + entry.Path;

        private bool Failing(FileEntry entry) => failingPaths.Contains(entry.Path);
    }

    [Fact]
    public void Fct01_HashFalhoEmUmMembro_GrupoInteiroUnresolved_NuncaDecisao()
    {
        var root = CriarArvore(("a/Rel.bin", "AAA"), ("b/Rel.bin", "AAA"));
        try
        {
            var pipeline = new ScanPipeline(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()),
                new FlakyHasher(root + "/b/Rel.bin"),
                streams: null);

            var result = pipeline.Run(root);

            Assert.Empty(result.IdenticalDuplicates);
            Assert.Empty(result.RealConflicts);

            var group = Assert.Single(result.UnresolvedGroups);
            var member = Assert.Single(group.Members);
            Assert.Equal("/b/Rel.bin", member.Path.EndsWith("/b/Rel.bin") ? "/b/Rel.bin" : member.Path);
            Assert.Contains("simulado", member.Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Fct02_ScanSaudavel_UnresolvedVazio()
    {
        var root = CriarArvore(("a/X.txt", "um"), ("b/X.txt", "um"), ("unico.txt", "dois"));
        try
        {
            var pipeline = new ScanPipeline(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()),
                new Blake3Hasher(),
                streams: null);

            var result = pipeline.Run(root);

            Assert.Empty(result.UnresolvedGroups);
            Assert.Single(result.IdenticalDuplicates);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Fct03_TodosMembrosFalhando_GrupoUnresolvedComTodosMotivos()
    {
        var root = CriarArvore(("a/Y.bin", "CCC"), ("b/Y.bin", "CCC"));
        try
        {
            var pipeline = new ScanPipeline(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()),
                new FlakyHasher(root + "/a/Y.bin", root + "/b/Y.bin"),
                streams: null);

            var result = pipeline.Run(root);

            Assert.Empty(result.IdenticalDuplicates);
            Assert.Empty(result.RealConflicts);

            var group = Assert.Single(result.UnresolvedGroups);
            Assert.Equal(2, group.Members.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Fct04_ConteudoInacessivelNaAbertura_GrupoUnresolved_SemExcecaoAoChamador()
    {
        // TOCTOU T-04/T-05: conteúdo inacessível no momento do hash completo
        // (arquivo truncado/lockeado/removido entre L2 e L3) → fail-closed:
        // grupo unresolved, o scan NÃO aborta e NUNCA classifica sem dado.
        var root = CriarArvore(("a/Z.txt", "DELTA"), ("b/Z.txt", "DELTA"));
        try
        {
            var streamSource = new LockedFileStreamSource(root + "/b/Z.txt");
            var pipeline = new ScanPipeline(
                new OrderedFileEnumerator(new CrossPlatformEnumerator()),
                new Blake3Hasher(),
                streamSource);

            var result = pipeline.Run(root); // não lança

            Assert.Empty(result.IdenticalDuplicates);
            Assert.Empty(result.RealConflicts);

            var group = Assert.Single(result.UnresolvedGroups);
            Assert.Single(group.Members);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Fonte que recusa a abertura do caminho alvo (simula lock/corrida T-04).</summary>
    private sealed class LockedFileStreamSource(string lockedPath) : IStreamSource
    {
        public Stream OpenRead(FileEntry entry)
        {
            if (entry.Path.EndsWith(lockedPath) || lockedPath.EndsWith(entry.Path))
            {
                throw new IOException("simulado: arquivo lockeado por outro processo");
            }

            return File.OpenRead(entry.Path);
        }
    }

    private static string CriarArvore(params (string Rel, string Conteudo)[] files)
    {
        var root = Directory.CreateTempSubdirectory("cd-s115").FullName;
        foreach (var (rel, conteudo) in files)
        {
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, conteudo);
        }
        return root;
    }
}
