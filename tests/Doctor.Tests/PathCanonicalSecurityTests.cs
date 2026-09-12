namespace Doctor.Tests;

using Doctor.Core;

/// <summary>
/// S11-1 (t_17f56008) — Path traversal and hostile names (T-01): canonicalization and containment.
/// Source: docs/threat-model.md T-01/R2/R12; SPEC §7–§9 (scanner levels); ADR-0002
/// fail-closed; decisions D4/D5/D6 of the card.
///
/// Primitives under test:
/// - <see cref="PathCanonical"/>: single canonicalization + extended form \\?\, structural
///   combination with re-canonicalization and byte-by-byte containment (D4) — boundary of everything the
///   product moves/writes;
/// - <see cref="FileEntry.HasBidiControlChars"/>: structural bidi marker (D5);
/// - integration: <see cref="QuarantineService.Move"/> and <see cref="QuarantineService.Restore"/>
///   execute intact under hostile names and &gt; 260 chars, without touching anything outside root.
///
/// SEG-01 (P0): tree with "evil.txt." (trailing dot), RLO name and Cyrillic homoglyph;
/// quarantine + restore with validated containment; no path outside root touched.
/// SEG-02 (P0): path &gt; 260 chars; move and restore intact via extended form.
/// SEG-03 (P2): RLO name does not deceive JSON/GUI output (structural marker + JSON escape).
/// </summary>
[Trait("Category", "Security")]
public sealed class PathCanonicalSecurityTests : IDisposable
{
    private static readonly DateTimeOffset Frozen = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string root;
    private readonly QuarantineService service = new();

    public PathCanonicalSecurityTests() =>
        root = Directory.CreateTempSubdirectory("cd-t01-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------
    // SEG-01 — Byte-by-byte containment under hostile names (T-01, P0)
    // ------------------------------------------------------------------

    [Fact]
    public void Security_PathTraversal_HostileName_ContainedInRoot()
    {
        // ---- phase A: the primitive refuses traversal escape -----------------
        // (own test area, discarded at end of phase — phase B's honeypot is
        // created AFTER, so it is not deleted by this phase's cleanup)
        var phaseADir = Directory.CreateTempSubdirectory("cd-phasea-");
        var outsideRoot = Directory.CreateTempSubdirectory("cd-outside-");
        try
        {
            // Destination outside root: fail-closed.
            Assert.Throws<PathEscapeException>(
                () => PathCanonical.EnsureContained(
                    Path.Combine(outsideRoot.FullName, "x.txt"), root));

            // Canonical ".." that would exit root: fail-closed.
            Assert.Throws<PathEscapeException>(
                () => PathCanonical.EnsureContained(
                    Path.Combine(root, "..", "outside.txt"), root));

            // Structural combination is born inside canonical root...
            var destination = PathCanonical.Combine(root, "ConflictDoctor", "quarantine", "op-1", "payload");
            Assert.StartsWith(PathCanonical.CanonicalizeRoot(root), destination, StringComparison.Ordinal);

            // ...and injected absolute segment is NOT accepted as root swap:
            // the combination re-canonicalizes (explicit diversion) and containment rejects.
            var hijacked = PathCanonical.Combine(root, phaseADir.FullName);
            Assert.Throws<PathEscapeException>(() => PathCanonical.EnsureContained(hijacked, root));
        }
        finally
        {
            phaseADir.Delete(recursive: true);
        }

        // ---- phase B: real hostile tree; quarantine + restore contained -------
        var hostileDir = Directory.CreateDirectory(Path.Combine(root, "sub"));

        var pDot = CreateHostileFile(hostileDir.FullName, "evil.txt.");              // trailing dot
        var pRlo = CreateHostileFile(hostileDir.FullName, "fdp\u202Eexe.pdf");       // U+202E RTL override
        var pHomoglyph = CreateHostileFile(hostileDir.FullName, "\u0430rquivo.txt"); // Cyrillic 'а'

        // Honeypot outside root: must remain untouched during the ENTIRE operation.
        var honeypot = Path.Combine(outsideRoot.FullName, "honeypot.txt");
        File.WriteAllBytes(honeypot, [0xCA, 0xFE]);
        var honeypotBytes = File.ReadAllBytes(honeypot);
        var honeypotMtime = File.GetLastWriteTimeUtc(honeypot);

        var items = new List<QuarantineItem>();
        foreach (var path in new[] { pDot, pRlo, pHomoglyph })
        {
            items.Add(new QuarantineItem(
                Entry(path),
                Reason: "hostile name (T-01)",
                Rule: "R2"));
        }

        var result = service.Move(items, new QuarantinePlan(root, Frozen), CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Equal(items.Count, result.MovedPaths.Count);

        // Every path produced by the operation stays INSIDE root, byte-by-byte.
        foreach (var moved in result.MovedPaths)
        {
            PathCanonical.EnsureContained(moved, root); // throws if escaping
        }

        // Restore returns originals byte-exact (identical names in UTF-16)...
        var restored = service.Restore(result.OperationId, root, CancellationToken.None);
        Assert.Equal(items.Count, restored.RestoredPaths.Count);

        foreach (var original in new[] { pDot, pRlo, pHomoglyph })
        {
            var expected = Path.GetFullPath(original);
            Assert.True(File.Exists(expected), $"expected back on disk: {expected}");
            Assert.Contains(expected, restored.RestoredPaths, StringComparer.Ordinal);
        }

        // ...the second containment pass remains closed...
        foreach (var restoredPath in restored.RestoredPaths)
        {
            PathCanonical.EnsureContained(restoredPath, root);
        }

        // ...and no path outside root was touched: honeypot intact, bytes and mtime unchanged.
        Assert.Equal(new[] { honeypot }, Directory.GetFiles(outsideRoot.FullName, "*", SearchOption.AllDirectories));
        Assert.Equal(honeypotBytes, File.ReadAllBytes(honeypot));
        Assert.Equal(honeypotMtime, File.GetLastWriteTimeUtc(honeypot));

        // Hostile names preserved EXACTLY as the filesystem gave them (T-01 mitigation (c)):
        // nothing was silently "fixed" — trailing dot, RLO and homoglyph come back identical.
        Assert.Equal(3, Directory.GetFiles(hostileDir.FullName).Length);

        // Manifest: byte-exact names, strict encoder did not alter decoded content.
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        var pathsInManifest = json.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("original_path").GetString())
            .ToArray();
        Assert.Equal(
            items.Select(i => i.Entry.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            pathsInManifest);
    }

    // ------------------------------------------------------------------
    // SEG-02 — > 260 chars with extended prefix, no truncation (P0)
    // ------------------------------------------------------------------

    [Fact]
    public void Security_LongPath_Over260Chars_ExtendedPrefixNoTruncation()
    {
        // Components of ~52 chars until final path exceeds 260 characters.
        const string component = "long-component-for-long-path-test-0123456789";
        var depth = Math.Max(1, (280 - root.Length) / (component.Length + 1));
        var currentDir = root;
        for (var i = 0; i < depth; i++)
        {
            currentDir = Path.Combine(currentDir, component);
        }
        Directory.CreateDirectory(currentDir);

        var finalName = "final-report-of-the-case-with-very-long-name.dat";
        var longPath = Path.Combine(currentDir, finalName);

        var content = "full content of the long file"u8.ToArray();
        File.WriteAllBytes(PathCanonical.ToExtendedLength(longPath), content);

        Assert.True(
            longPath.Length > 260,
            $"test requires path > 260 chars; got {longPath.Length}");

        var items = new List<QuarantineItem> { new(Entry(longPath), "long path", "R2") };

        var result = service.Move(items, new QuarantinePlan(root, Frozen), CancellationToken.None);
        Assert.Equal("completed", result.Status);
        Assert.Single(result.MovedPaths);

        // Restore recreates the deep tree without truncating a single character.
        var restored = service.Restore(result.OperationId, root, CancellationToken.None);
        Assert.Single(restored.RestoredPaths);

        var back = Path.GetFullPath(longPath);
        Assert.True(File.Exists(back), "payload must return to path > 260 intact");
        Assert.Equal(content, File.ReadAllBytes(back));
        Assert.Equal(longPath.Length, restored.RestoredPaths[0].Length);
    }

    // ------------------------------------------------------------------
    // SEG-03 — RLO name does not deceive JSON/GUI output (T-01/R12, P2)
    // ------------------------------------------------------------------

    [Fact]
    public void Report_BidiControlChars_EscapedInJsonAndGui()
    {
        // Structural marker (D5): the NAME is never mutated; the flag exposes the risk.
        var rloName = "fdp\u202Eexe.pdf";

        Assert.False(PathCanonical.HasBidiControlChars("report.pdf"));
        Assert.False(PathCanonical.HasBidiControlChars("photo v2.jpg"));
        Assert.False(PathCanonical.HasBidiControlChars(null));
        Assert.True(PathCanonical.HasBidiControlChars(rloName));
        // Isolates/marks are also bidi control (fixed auditable list).
        Assert.True(PathCanonical.HasBidiControlChars("a\u2066b\u2069.pdf"));
        Assert.True(PathCanonical.HasBidiControlChars("note\u200Ffinal.docx"));

        var dir = Directory.CreateDirectory(Path.Combine(root, "rlo"));
        var path = CreateHostileFile(dir.FullName, rloName);
        var entry = Entry(path);

        Assert.True(entry.HasBidiControlChars);

        // JSON with the SAME strict encoder as report/manifest (JavaScriptEncoder.Default):
        // U+202E comes out escaped (\u202e) — the consumer never sees bytes that reorder
        // rendering; after decoding, the name remains byte-exact and the structural
        // flag accompanies it (rendering belongs to EPIC 10).
        var json = System.Text.Json.JsonSerializer.Serialize(
            new { name = entry.Path, has_bidi_control_chars = entry.HasBidiControlChars },
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            });

        Assert.DoesNotContain('\u202E', json);
        Assert.Contains("\\u202e", json, StringComparison.OrdinalIgnoreCase);

        var decoded =
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
        Assert.Equal(entry.Path, decoded["name"].GetString());
        Assert.True(decoded["has_bidi_control_chars"].GetBoolean());
    }

    // ------------------------------------------------------------------
    // infrastructure
    // ------------------------------------------------------------------

    /// <summary>L0 snapshot consistent with the contract (immutable FileEntry).</summary>
    private FileEntry Entry(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(PathCanonical.ToExtendedLength(full));

        return new FileEntry
        {
            Path = full,
            Size = info.Length,
            MtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            Attributes = info.Attributes,
            VolumeId = "test-vol",
            FileId = $"fid-{Guid.NewGuid():N}",
        };
    }

    /// <summary>Creates a file whose name raw Win32 would refuse (trailing dot/RLO/homoglyph), via extended form.</summary>
    private static string CreateHostileFile(string dir, string name)
    {
        var destination = Path.Combine(dir, name);
        File.WriteAllBytes(PathCanonical.ToExtendedLength(destination), [0x63, 0x64, 0x2D]);
        return destination;
    }
}
