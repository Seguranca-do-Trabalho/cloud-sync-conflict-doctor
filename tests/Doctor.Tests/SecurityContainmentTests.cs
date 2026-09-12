namespace Doctor.Tests;

using System.Text.Json;
using Doctor.Core;
using Xunit;

/// <summary>
/// S11-6a (card t_218a0218) — SEG-01 and SEG-08 from GATE 5 matrix
/// (docs/security-audit-gate5.md §2; addenda T-12a/T-12b §4.1):
///
/// SEG-01 — <see cref="Security_PathTraversal_HostileName_ContainedInRoot"/>
/// (threat-model T-01, rule R2): byte-by-byte containment by the canonical root prefix
/// BEFORE each move/restore. Tree with trailing dot/space name (vector \\?\),
/// RLO U+202E name and Cyrillic homoglyph go through quarantine AND restore; forged
/// manifest with original_path outside root is REJECTED without touching anything; no path
/// outside root is touched in the entire operation.
///
/// SEG-08 — <see cref="Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack"/>
/// (threat-model T-04, rules R4/R5; contract R5): hook injects content swap
/// BETWEEN the pre-move hash and the move; asserts rollback executed, source back at
/// original, operation FAILS (nothing declared success) and auditable evidence
/// hash_pre_move != hash_post_move in the partial manifest.
/// </summary>
public sealed class SecurityContainmentTests : IDisposable
{
    private static readonly DateTimeOffset FrozenTimestamp =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTime FixedEntryMtime =
        new(2026, 8, 20, 10, 30, 0, DateTimeKind.Utc);

    private readonly string _root;

    /// <summary>Honeypot directory OUTSIDE root: any containment escape touches these bytes.</summary>
    private readonly string _outsideRoot;

    public SecurityContainmentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"s116a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _outsideRoot = Path.Combine(Path.GetTempPath(), $"s116a-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outsideRoot);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _outsideRoot })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup: OS temp reclaims later
            }
        }
    }

    // ------------------------------------------------------------------
    // SEG-01 (T-12a / R2): byte-by-byte containment under hostile names
    // ------------------------------------------------------------------
    [Fact]
    public void Security_PathTraversal_HostileName_ContainedInRoot()
    {
        // T-01 vectors creatable on POSIX filesystem: trailing dot/space (Win32
        // without \\?\ resolves as DIFFERENT file), RLO U+202E (display name deceives) and
        // Cyrillic homoglyph ('а' U+0430 ≠ 'a' latin).
        var trailingDotSpace = CreateFile("evil.txt. ", Content(0xE1));
        var rlo = CreateFile("\u202Eexe.pdf", Content(0xE2));
        var homoglyph = CreateFile("\u0430rquivo.txt", Content(0xE3));
        var hostileNames = new[] { trailingDotSpace, rlo, homoglyph };

        // Honeypot outside root: must remain untouched during the ENTIRE operation.
        var honeypot = Path.Combine(_outsideRoot, "honeypot.txt");
        File.WriteAllBytes(honeypot, [0xCA, 0xFE]);
        var honeypotBytes = File.ReadAllBytes(honeypot);
        var honeypotMtime = File.GetLastWriteTimeUtc(honeypot);

        var svc = new QuarantineService();

        // ---- act 1: quarantine + restore execute through hostile paths ------------
        var result = svc.Move(
            hostileNames.Select(c => new QuarantineItem(Entry(c), "IDENTICAL_DUPLICATE", "KEEP_NEWEST")).ToArray(),
            DefaultPlan());
        Assert.Equal(3, result.MovedPaths.Count);

        var restoration = svc.Restore(result.OperationId, _root);
        Assert.Equal(3, restoration.RestoredPaths.Count);

        // byte-by-byte containment: every touched path starts with the canonical root prefix
        // (Ordinal comparison on full form, trailing separator guaranteed).
        var rootPrefix = WithTrailingSeparator(Path.GetFullPath(_root));
        Assert.All(
            restoration.RestoredPaths.Concat(result.MovedPaths),
            c => Assert.True(
                Path.GetFullPath(c).StartsWith(rootPrefix, StringComparison.Ordinal),
                $"touched path outside canonical root: {c}"));

        // no path outside root was touched: honeypot intact, bytes and mtime unchanged
        Assert.Equal(new[] { honeypot }, Directory.GetFiles(_outsideRoot, "*", SearchOption.AllDirectories));
        Assert.Equal(honeypotBytes, File.ReadAllBytes(honeypot));
        Assert.Equal(honeypotMtime, File.GetLastWriteTimeUtc(honeypot));

        // hostile names preserved EXACTLY (T-01 mitigation (c): no silent correction)
        Assert.All(hostileNames, c => Assert.True(File.Exists(c), $"hostile not restored: {c}"));
        using var json = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        var pathsInManifest = json.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("original_path").GetString())
            .ToArray();
        Assert.Equal(
            hostileNames.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            pathsInManifest);

        // ---- act 2: forged manifest with original_path outside root is rejected ----
        // Quarantine is tool metadata itself, but restore trusts nothing:
        // destination outside canonical prefix ⇒ fail-closed BEFORE any touch.
        var victim = CreateFile("docs/victim.txt", Content(0xE4));
        var honeypotOp = svc.Move(
            [new QuarantineItem(Entry(victim), "REAL_CONFLICT", "KEEP_NEWEST")],
            DefaultPlan());

        var forgedDestination = Path.Combine(_outsideRoot, "escape.txt");
        RewriteOriginalPath(honeypotOp.ManifestPath, forgedDestination);

        Assert.Throws<Doctor.Core.QuarantineContainmentException>(
            () => svc.Restore(honeypotOp.OperationId, _root));

        // fail-closed: NOTHING written outside root, payload remains in quarantine
        Assert.False(File.Exists(forgedDestination), "forged restore wrote outside root");
        Assert.True(File.Exists(SinglePayload(honeypotOp)), "payload disappeared without read rollback");
        Assert.Equal(new[] { honeypot }, Directory.GetFiles(_outsideRoot, "*", SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------------
    // SEG-08 (T-12b / R5): content swap in hash→move window ⇒ rollback
    // ------------------------------------------------------------------
    [Fact]
    public void Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack()
    {
        var goodContent = Content(0xB0);
        var badContent = Content(0xD1);
        var goodHash = Convert.ToHexString(Blake3.Hasher.Hash(goodContent).AsSpan()).ToLowerInvariant();
        var badHash = Convert.ToHexString(Blake3.Hasher.Hash(badContent).AsSpan()).ToLowerInvariant();
        Assert.NotEqual(goodHash, badHash);

        var path = CreateFile("docs/report.docx", goodContent);
        var entry = Entry(path); // L0 snapshot captured over GOOD content

        // T-04 WINDOW hook: the pre-move hash reads via openReadOverride (chain of
        // the gate — good content); the moveOverride swaps content at the path BEFORE
        // File.Move. The swap happens ONCE only — exactly between the pre-move hash
        // and the move; the rollback goes through the same _move without re-triggering.
        var swapped = false;
        var svc = new QuarantineService(
            openReadOverride: p => p == path
                ? new MemoryStream(goodContent)
                : File.OpenRead(p),
            moveOverride: (source, destination) =>
            {
                if (!swapped)
                {
                    swapped = true;
                    Assert.Equal(path, source);
                    File.WriteAllBytes(path, badContent); // swap in the window
                }

                File.Move(source, destination);
            });

        var exception = Assert.Throws<QuarantineRollbackException>(() => svc.Move(
            [new QuarantineItem(entry, "IDENTICAL_DUPLICATE", "KEEP_NEWEST")],
            DefaultPlan()));

        Assert.Equal(path, exception.OriginalPath);

        // rollback executed: source exists BACK at origin (without rollback the
        // file would have stayed in quarantine); the bytes present are exactly those
        // that were moved and returned — proof of complete round-trip.
        Assert.True(File.Exists(path));
        Assert.Equal(badContent, File.ReadAllBytes(path));

        // nothing declared success: no definitive <op_id> directory was published
        var quarantineRoot = Path.Combine(_root, "ConflictDoctor", "quarantine");
        string[] published = Directory.Exists(quarantineRoot)
            ? Directory.GetDirectories(quarantineRoot)
                .Where(d => !Path.GetFileName(d).StartsWith("staging-", StringComparison.Ordinal))
                .ToArray()
            : [];
        Assert.Empty(published);

        // auditable evidence in partial manifest: status FAILED +
        // hash_pre_move ≠ hash_post_move recorded (contract R5)
        using var json = JsonDocument.Parse(File.ReadAllBytes(exception.PartialManifestPath));
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Equal(path, item.GetProperty("original_path").GetString());
        Assert.Equal(goodHash, item.GetProperty("hash_pre_move").GetString());
        Assert.Equal(badHash, item.GetProperty("hash_post_move").GetString());
        Assert.NotEqual(
            item.GetProperty("hash_pre_move").GetString(),
            item.GetProperty("hash_post_move").GetString());

        // external honeypot remains untouched
        Assert.Empty(Directory.GetFiles(_outsideRoot, "*", SearchOption.AllDirectories));
    }

    // ==================================================================
    // test infrastructure
    // ==================================================================

    private QuarantinePlan DefaultPlan() => new(_root, FrozenTimestamp);

    private static string WithTrailingSeparator(string directory) =>
        directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

    private static byte[] Content(byte seed) =>
        Enumerable.Range(0, 2048).Select(i => (byte)(seed + (i % 89))).ToArray();

    private string CreateFile(string relativePath, byte[] content)
    {
        var absolute = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, content);
        File.SetLastWriteTimeUtc(absolute, FixedEntryMtime);
        return absolute;
    }

    private FileEntry Entry(string path)
    {
        var info = new FileInfo(path);
        return new FileEntry
        {
            Path = path,
            Size = info.Length,
            MtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc),
            Attributes = FileAttributes.Normal,
            VolumeId = "s116a-volume",
            FileId = path,
        };
    }

    private static void RewriteOriginalPath(string manifestPath, string newDestination)
    {
        // Forges the manifest swapping ONLY the original_path of the first item
        // (simulates T-01 vector: consumer cannot trust metadata).
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = doc.RootElement;

        var manifest = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name != "items")
            {
                manifest[prop.Name] = prop.Value.Clone();
            }
        }

        var items = new List<Dictionary<string, object?>>();
        var first = true;
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            var dict = new Dictionary<string, object?>();
            foreach (var prop in item.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }

            if (first)
            {
                dict["original_path"] = newDestination;
                first = false;
            }

            items.Add(dict);
        }

        manifest["items"] = items;
        File.WriteAllBytes(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    private static string SinglePayload(QuarantineOperationResult move)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(move.ManifestPath));
        var relative = json.RootElement.GetProperty("items")[0]
            .GetProperty("quarantine_path").GetString()!;
        return Path.Combine(
            move.QuarantineDirectory,
            relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
