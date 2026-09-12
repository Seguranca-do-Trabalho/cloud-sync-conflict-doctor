namespace Doctor.Core;

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

/// <summary>Item to move to quarantine: L0 snapshot + auditable decision
/// (SPEC §17/§18; ADR-0002). <c>Reason</c>/<c>Rule</c> enter the manifest.</summary>
public sealed record QuarantineItem(FileEntry Entry, string Reason, string Rule);

/// <summary>
/// Quarantine plan (docs/test-strategy.md §2.5): scanned root and operation
/// timestamp received by injection — never globally resolved. The frozen timestamp
/// makes <see cref="QuarantineService.Move"/> testable and deterministic.
/// </summary>
public sealed record QuarantinePlan(string RootPath, DateTimeOffset TimestampUtc);

/// <summary>Complete move result: §18 operation paths.</summary>
public sealed record QuarantineOperationResult(
    string OperationId,
    string QuarantineDirectory,
    string ManifestPath,
    string Status,
    IReadOnlyList<string> MovedPaths,
    IReadOnlyList<string> SkippedStaleMetadata);

/// <summary>Restore result (§18; ADR-0010 §4).</summary>
public sealed record RestoreResult(
    string OperationId,
    IReadOnlyList<string> RestoredPaths);

/// <summary>Restored item with path deviation recorded in history.</summary>
public sealed record RestoredItem(
    string OriginalPath,
    string ActualPath,
    bool UsedFallbackSuffix);

/// <summary>
/// Aborted restore: some item's destination is occupied. NOTHING is overwritten
/// (ADR-0002 item 3, ADR-0010 §4) — the exception carries the conflicting pair.
/// </summary>
public sealed class RestoreConflictException : InvalidOperationException
{
    public RestoreConflictException(string originalPath, string occupiedBy)
        : base($"Restore cancelled: destination already occupied, NEVER overwrite " +
               $"(ADR-0002 item 3 / ADR-0010 4): '{originalPath}' occupied by '{occupiedBy}'.")
    {
        OriginalPath = originalPath;
        OccupiedBy = occupiedBy;
    }

    /// <summary>Original path intended for restore.</summary>
    public string OriginalPath { get; }

    /// <summary>Path of the payload that could not be returned.</summary>
    public string OccupiedBy { get; }
}

/// <summary>
/// Byte-by-byte containment violation (threat-model T-01, mitigation (b); rule R2):
/// a move/resolve/restore destination path does not start with the canonical prefix
/// of the authorized root. Thrown BEFORE any touch — fail-closed, nothing is
/// moved or written.
/// </summary>
public sealed class QuarantineContainmentException : InvalidOperationException
{
    public QuarantineContainmentException(string path, string rootPrefix)
        : base($"Containment violated: destination '{path}' is outside the canonical prefix " +
               $"'{rootPrefix}'. Operation refused without touching anything (T-01/R2).")
    {
        Path = path;
        RootPrefix = rootPrefix;
    }

    /// <summary>Refused path.</summary>
    public string Path { get; }

    /// <summary>Required canonical prefix.</summary>
    public string RootPrefix { get; }
}

/// <summary>
/// Post-move hash divergence (threat-model T-04, rule R5): bytes that arrived at
/// quarantine differ from the hash captured immediately before the move. The source was
/// ROLLED BACK (moved back) and the operation is FAILED — nothing is declared success.
/// The partial manifest carries hash_pre_move ≠ hash_post_move for audit.
/// </summary>
public sealed class QuarantineRollbackException : QuarantinePartialException
{
    public QuarantineRollbackException(
        string partialManifestPath,
        string originalPath,
        string hashPreMove,
        string hashPostMove)
        : base(partialManifestPath,
               $"Post-move TOCTOU divergence on '{originalPath}': hash_pre_move {hashPreMove} " +
               $"!= hash_post_move {hashPostMove}. Rollback executed; operation FAILED (R5).")
    {
        OriginalPath = originalPath;
        HashPreMove = hashPreMove;
        HashPostMove = hashPostMove;
    }

    /// <summary>Original path whose operation was rolled back.</summary>
    public string OriginalPath { get; }

    /// <summary>Hash captured at the source immediately before the move.</summary>
    public string HashPreMove { get; }

    /// <summary>Hash recalculated on the payload already in quarantine.</summary>
    public string HashPostMove { get; }
}

/// <summary>
/// Move failed mid-operation: already-moved items remain registered in an
/// honest PARTIAL manifest; nothing moved goes unrecorded (ADR-0002 item 4;
/// ADR-0010 §3). The exception points to the partial manifest for audit.
/// </summary>
public class QuarantinePartialException : InvalidOperationException
{
    public QuarantinePartialException(string partialManifestPath, string message)
        : base($"{message} Partial manifest: {partialManifestPath}")
        => PartialManifestPath = partialManifestPath;

    /// <summary>Manifest with "partial" status reflecting exactly what was moved.</summary>
    public string PartialManifestPath { get; }
}

/// <summary>
/// Quarantine service (EPIC 08 / card T15; SPEC §18; ADR-0002 + ADR-0010):
///
/// • ONLY destructive-permissive product API is <see cref="File.Move"/> to
///   <c>&lt;root&gt;/ConflictDoctor/quarantine/&lt;op_id&gt;/payload</c>; no
///   File.Delete/Directory.Delete exists in this module (static guard QRT).
/// • Deterministic operation_id: <c>yyyyMMddTHHmmssZ-&lt;8 hex BLAKE3 truncated
///   from manifest content&gt;</c> — same state ⇒ same id (ADR-0010 §1).
/// • Canonical order: items sorted by path in UTF-8 bytes
///   (<see cref="StringComparer.Ordinal"/>) before any move.
/// • TOCTOU size+mtime revalidation against L0 snapshot before each move;
///   divergent ⇒ item skipped and recorded (<c>skipped_stale_metadata</c>).
/// • Mid-failure ⇒ honest partial manifest + <see cref="QuarantinePartialException"/>.
/// </summary>
public sealed class QuarantineService
{
    private const int CopyBufferSize = 256 * 1024;

    /// <summary>
    /// Manifest JSON options. The encoder is the STRICT <see cref="JavaScriptEncoder.Default"/>
    /// (S11-1/SEG-03, R12): escapes ALL non-ASCII and control characters — including
    /// bidi (U+202E etc.) — so that no raw byte capable of reordering the
    /// consumer's rendering (RMM/GUI) appears in the document. Names remain
    /// byte-exact after UTF-8 decode; the cost is only escape size. The previous relaxed
    /// encoder emitted raw U+202E in the manifest — T-01 vector.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>Injects a custom opener (tests); production uses File.OpenRead.</summary>
    private readonly Func<string, Stream> _openRead;

    /// <summary>Injectable mover (tests simulate disk/permission failure).</summary>
    private readonly Action<string, string> _move;

    /// <param name="openReadOverride">Read source for hash/validation; production uses File.OpenRead.</param>
    /// <param name="moveOverride">Move primitive; production uses File.Move(source, destination).</param>
    public QuarantineService(
        Func<string, Stream>? openReadOverride = null,
        Action<string, string>? moveOverride = null)
    {
        _openRead = openReadOverride ?? (static path => File.OpenRead(path));
        _move = moveOverride ?? ((source, destination) => File.Move(source, destination));
    }

    /// <summary>
    /// Moves each item to <paramref name="plan"/>.RootPath +
    /// ConflictDoctor/quarantine/&lt;op_id&gt;/payload/, writes the JSON manifest
    /// atomically and returns the result. NEVER deletes: if the move fails mid-way,
    /// the state is recorded in a partial manifest and the exception propagates
    /// (fail-closed, ADR-0002 item 4).
    /// </summary>
    public QuarantineOperationResult Move(
        IReadOnlyList<QuarantineItem> items,
        QuarantinePlan plan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(plan);

        if (items.Count == 0)
        {
            throw new ArgumentException("No items to quarantine.", nameof(items));
        }

        ct.ThrowIfCancellationRequested();

        // Canonical order by path in UTF-8 bytes (ADR-0003 rule 1) — never arrival
        // order, never filesystem order.
        var sorted = items
            .OrderBy(i => i.Entry.Path, StringComparer.Ordinal)
            .ToArray();

        var opIdBase = plan.TimestampUtc.ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        // Provisional directory without deterministic suffix yet (the id depends on
        // the manifest, which depends on the final paths): creates with a unique
        // temporary name based on THE PLAN, and renames at the end. In two runs of
        // the same state with the same plan, the final directory has the SAME name.
        // STRUCTURAL combination (S11-1/R2/D6): no raw Path.Combine on input
        // data — the entire chain is born re-canonicalized in extended form.
        var quarantineRoot = PathCanonical.Combine(
            plan.RootPath, "ConflictDoctor", "quarantine");
        Directory.CreateDirectory(quarantineRoot);

        // Payload staging: files need a physical destination BEFORE the final id
        // exists. Uses an ephemeral working directory inside the dated quarantine;
        // on close, renames to the final <op_id> directory.
        var stagingName = $"staging-{Guid.NewGuid():N}";
        var stagingDir = PathCanonical.Combine(quarantineRoot, stagingName);
        var payloadDir = PathCanonical.Combine(stagingDir, "payload");
        Directory.CreateDirectory(payloadDir);

        var records = new List<Dictionary<string, object?>>();
        var moved = new List<string>();
        var skipped = new List<string>();
        var payloadMapping = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            for (var index = 0; index < sorted.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var item = sorted[index];
                var entry = item.Entry;

                // Placeholder gate (SPEC §6): placeholder content is NEVER read.
                if (entry.IsPlaceholder || PlaceholderPolicy.IsPlaceholder(entry))
                {
                    throw new PlaceholderViolationException(
                        entry.Path, "quarantine would try to read placeholder content:");
                }

                // TOCTOU (ADR-0010 §3): revalidates size+mtime against L0 snapshot.
                var info = new FileInfo(entry.Path);
                if (!info.Exists
                    || info.Length != entry.Size
                    || info.LastWriteTimeUtc != entry.MtimeUtc.UtcDateTime)
                {
                    skipped.Add(entry.Path); // file stays where it is; honest record
                    continue;
                }

                // Sequential opaque name (ADR-0010 §1): no relation to original.
                var payloadName = $"{index + 1:D4}.dat";
                var absoluteDestination = PathCanonical.Combine(payloadDir, payloadName);

                // Byte-by-byte containment (T-01/R2) BEFORE the move — via the single
                // PathCanonical primitive (D6), delegated by EnsureContainment to preserve
                // this module's public contract (QuarantineContainmentException):
                // destination AND source must fall under the canonical root prefix;
                // otherwise, fail-closed without touching anything.
                EnsureContainment(absoluteDestination, plan.RootPath);
                EnsureContainment(entry.Path, plan.RootPath);

                // Pre-move hash of the ORIGINAL immediately before the move (T-04/R5),
                // via the same read chain as the gate (_openRead).
                var hashPreMove = FullHashBlake3Streaming(entry.Path, ct);

                _move(entry.Path, absoluteDestination);

                moved.Add(entry.Path);
                payloadMapping[entry.Path] = $"payload/{payloadName}";

                // Full BLAKE3 hash of the PAYLOAD already in quarantine (single source
                // of truth in the manifest; streaming, never loads entire file in memory).
                // It is also the POST-MOVE VERIFICATION of R5: divergent from pre-move ⇒
                // rollback (move back) and operation FAILED — nothing is declared success.
                var hash = FullHashBlake3Streaming(absoluteDestination, ct);
                if (!string.Equals(hash, hashPreMove, StringComparison.Ordinal))
                {
                    RollbackPostMove(
                        stagingDir,
                        payloadDir,
                        payloadName,
                        entry,
                        item.Reason,
                        item.Rule,
                        hashPreMove,
                        hash);
                    throw new QuarantineRollbackException(
                        Path.Combine(stagingDir, "manifest-partial.json"),
                        entry.Path,
                        hashPreMove,
                        hash);
                }

                records.Add(new Dictionary<string, object?>
                {
                    ["original_path"] = entry.Path,
                    ["quarantine_path"] = $"payload/{payloadName}",
                    ["size"] = entry.Size,
                    ["mtime_utc"] = FormatTimestamp(entry.MtimeUtc),
                    ["hash"] = hash,
                    ["algorithm"] = "BLAKE3",
                    ["hash_version"] = 1,
                    ["reason"] = item.Reason,
                    ["rule"] = item.Rule,
                });
            }

            // Manifest v1 (ADR-0010 §2): keys in exact schema order.
            var manifest = new Dictionary<string, object?>
            {
                ["manifest_version"] = 1,
                ["operation_id"] = null!, // filled after deriving id from content
                ["created_utc"] = FormatTimestamp(plan.TimestampUtc),
                ["items"] = records,
                // status reflects the published operation; failures abort before (honest partial)
                ["status"] = "completed",
            };

            // TOCTOU audit (ADR-0010 §3): items skipped due to stale metadata enter
            // the published manifest. Key present ONLY when there are skipped —
            // the common case remains with exactly the 5 keys of ADR-0010 §2;
            // same state ⇒ same keys ⇒ same id (determinism).
            if (skipped.Count > 0)
            {
                manifest["skipped_stale_metadata"] = skipped.ToArray();
            }

            var manifestWithoutId = Serialize(manifest, placeholderOpId: true);

            // operation_id = timestamp + 8 hex BLAKE3 of manifest body WITHOUT the id
            // (impossible self-reference): same state ⇒ same body ⇒ same id.
            var suffixHex = Convert.ToHexString(Blake3.Hasher.Hash(manifestWithoutId).AsSpan())
                .ToLowerInvariant()[..8];
            var operationId = $"{opIdBase}-{suffixHex}";
            manifest["operation_id"] = operationId;

            var manifestFinal = Serialize(manifest, placeholderOpId: false);

            // ATOMIC manifest write (ADR-0010 §2): .tmp → File.Move overwrite:false.
            var manifestPathFinalStaging = Path.Combine(stagingDir, "manifest.json");
            var tmpPath = manifestPathFinalStaging + ".tmp";
            File.WriteAllBytes(tmpPath, manifestFinal);
            File.Move(tmpPath, manifestPathFinalStaging, overwrite: false);

            // Publishes the final <op_id> directory: renames staging → final.
            var finalDir = Path.Combine(quarantineRoot, operationId);
            Directory.Move(stagingDir, finalDir);

            return new QuarantineOperationResult(
                operationId,
                finalDir,
                Path.Combine(finalDir, "manifest.json"),
                moved.Count > 0 || skipped.Count > 0 ? "completed" : "completed",
                moved.ToArray(),
                skipped.ToArray());
        }
        catch (Exception exception) when (exception is not QuarantinePartialException
                                       && exception is not PlaceholderViolationException
                                             || true)
        {
            // Fail-closed (ADR-0002 item 4): records the actual state in an
            // honest PARTIAL manifest inside staging and propagates. Nothing moved
            // goes unrecorded; nothing partial goes untraced.
            var partialPath = Path.Combine(stagingDir, "manifest-partial.json");
            try
            {
                var partial = new Dictionary<string, object?>
                {
                    ["manifest_version"] = 1,
                    ["operation_id"] = "(partial)",
                    ["created_utc"] = FormatTimestamp(plan.TimestampUtc),
                    ["items"] = records,
                    ["status"] = "partial",
                    ["skipped_stale_metadata"] = skipped.ToArray(),
                };

                if (!File.Exists(partialPath))
                {
                    File.WriteAllBytes(partialPath, SerializePartial(partial));
                }
            }
            catch
            {
                // not even the partial record was possible: propagates the original failure —
                // never masks one error with another.
            }

            throw exception is QuarantinePartialException
                ? exception
                : new QuarantinePartialException(partialPath, Message(exception));
        }
        finally
        {
            // Remaining staging (failure) is NOT deleted — preserves evidence.
            if (Directory.Exists(stagingDir))
            {
                // kept: ADR-0002 audit requires nothing silently disappears
            }
        }
    }

    /// <summary>
    /// Restores all items in the operation to their <c>original_path</c>.
    /// TOTAL PRE-CHECK before the first touch: if ANY destination is
    /// occupied, throws <see cref="RestoreConflictException"/> without touching
    /// ANYTHING (neither occupant, nor payload, nor manifest). Never overwrites (ADR-0002
    /// item 3). Validates BLAKE3 hash of each payload before moving back.
    /// </summary>
    public RestoreResult Restore(string operationId, string rootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var operationDir = Path.Combine(rootPath, "ConflictDoctor", "quarantine", operationId);
        var manifestPath = Path.Combine(operationDir, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Operation manifest not found: {manifestPath}", manifestPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = doc.RootElement;

        var status = root.GetProperty("status").GetString();
        if (status == "partial")
        {
            throw new InvalidOperationException(
                $"Operation {operationId} is PARTIAL — manual restore required; " +
                "there is no way to automatically restore items that were never moved.");
        }

        var items = root.GetProperty("items");
        if (items.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"Operation {operationId} has no restorable items.");
        }

        // ---- phase 1: validate EVERYTHING (hash + free destinations) without touching anything --------
        var pending = new List<(string AbsolutePayload, string Destination, string ExpectedHash)>();

        // Byte-by-byte containment via the SINGLE PathCanonical primitive (S11-1/D6): each
        // item's destination must fall under the reported canonical root — forged or
        // corrupted manifest with external original_path is rejected BEFORE
        // any touch, without moving or writing anything.

        foreach (var item in items.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            // Structural combination + containment (T-01/R2/D6) BEFORE reading: the
            // manifest is EXTERNAL data — hostile quarantine_path/original_path never
            // reach the disk outside root, not even to read or move.
            var relative = item.GetProperty("quarantine_path").GetString()!;
            var absolutePayload = PathCanonical.Combine(operationDir, relative.Replace('/', Path.DirectorySeparatorChar));
            var destination = item.GetProperty("original_path").GetString()!;
            var expectedHash = item.GetProperty("hash").GetString()!;

            EnsureContainment(absolutePayload, rootPath);
            EnsureContainment(destination, rootPath);

            // Integrity validation: corrupted payload ⇒ restore refused.
            var currentHash = FullHashBlake3Streaming(absolutePayload, ct);
            if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Integrity FAILED for '{absolutePayload}': current hash {currentHash} " +
                    $"!= manifest {expectedHash}. Restore refused.");
            }

            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new RestoreConflictException(destination, absolutePayload);
            }

            pending.Add((absolutePayload, destination, expectedHash));
        }

        // ---- phase 2: move back, recreating missing parent directories ----------
        var restored = new List<string>();
        foreach (var (absolutePayload, destination, _) in pending)
        {
            ct.ThrowIfCancellationRequested();

            // Byte-by-byte containment on return as well (T-01/R2, D6): the manifest's
            // original_path is re-canonicalized and MUST fall under the reported root.
            EnsureContainment(destination, rootPath);
            EnsureContainment(absolutePayload, rootPath);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(absolutePayload, destination, overwrite: false);
            restored.Add(destination);
        }

        // ---- phase 3: mark restored in manifest (history never deleted) -----
        MarkRestored(manifestPath, pending.Select(p => p.Destination).ToArray(), operationId);

        return new RestoreResult(operationId, restored.ToArray());
    }

    // ------------------------------------------------------------------
    // internal infrastructure
    // ------------------------------------------------------------------

    /// <summary>
    /// Byte-by-byte containment (threat-model T-01 mitigation (b); rule R2) — DELEGATION
    /// to the SINGLE <see cref="PathCanonical.EnsureContained"/> primitive (D6 of card
    /// S11-1): no second containment implementation exists in the product. The
    /// <see cref="PathEscapeException"/> from the primitive is translated to
    /// <see cref="QuarantineContainmentException"/>, this module's public contract
    /// since S11-6a — same meaning (fail-closed before touching anything),
    /// same surface for consumers.
    /// </summary>
    private static void EnsureContainment(string path, string operationRoot)
    {
        try
        {
            PathCanonical.EnsureContained(path, operationRoot);
        }
        catch (PathEscapeException escape)
        {
            throw new QuarantineContainmentException(escape.RequestedPath, escape.Root);
        }
    }

    /// <summary>
    /// R5 rollback: moves the divergent payload BACK to the original path,
    /// removes the moved item record and writes the partial manifest with status
    /// "failed" and the auditable evidence hash_pre_move ≠ hash_post_move. Failure
    /// in the rollback itself propagates the original exception — never masks
    /// one error with another.
    /// </summary>
    private void RollbackPostMove(
        string stagingDir,
        string payloadDir,
        string payloadName,
        FileEntry entry,
        string reason,
        string rule,
        string hashPreMove,
        string hashPostMove)
    {
        var absolutePayload = Path.Combine(payloadDir, payloadName);

        try
        {
            if (File.Exists(absolutePayload) && !File.Exists(entry.Path))
            {
                _move(absolutePayload, entry.Path);
            }
        }
        finally
        {
            // Auditable evidence even if physical rollback fails (R11):
            // FAILED record with the hash_pre_move/hash_post_move chain.
            try
            {
                var partial = new Dictionary<string, object?>
                {
                    ["manifest_version"] = 1,
                    ["operation_id"] = "(partial)",
                    ["created_utc"] = FormatTimestamp(DateTimeOffset.UtcNow),
                    ["items"] = new List<Dictionary<string, object?>>
                    {
                        new()
                        {
                            ["original_path"] = entry.Path,
                            ["quarantine_path"] = $"payload/{payloadName}",
                            ["size"] = entry.Size,
                            ["mtime_utc"] = FormatTimestamp(entry.MtimeUtc),
                            ["hash_pre_move"] = hashPreMove,
                            ["hash_post_move"] = hashPostMove,
                            ["algorithm"] = "BLAKE3",
                            ["hash_version"] = 1,
                            ["reason"] = reason,
                            ["rule"] = rule,
                        },
                    },
                    ["status"] = "failed",
                };

                var partialPath = Path.Combine(stagingDir, "manifest-partial.json");
                if (!File.Exists(partialPath))
                {
                    File.WriteAllBytes(partialPath, SerializePartial(partial));
                }
            }
            catch
            {
                // not even the record was possible: the rollback/original exception prevails
            }
        }
    }

    private string FullHashBlake3Streaming(string path, CancellationToken ct)
    {
        using var stream = _openRead(path);
        using var hasher = Blake3.Hasher.New();

        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = stream.Read(rented, 0, rented.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Update(rented.AsSpan(0, read));
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }

        var hashBytes = hasher.Finalize();
        return Convert.ToHexString(hashBytes.AsSpan()).ToLowerInvariant(); // lowercase hex (ADR-0005 §1)
    }

    private static string FormatTimestamp(DateTimeOffset ts) =>
        ts.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static byte[] Serialize(Dictionary<string, object?> manifest, bool placeholderOpId)
    {
        if (placeholderOpId)
        {
            // DERIVABLE BODY: serializes with operation_id replaced by a fixed
            // marker — the id derives from actual content without self-reference.
            var copy = new Dictionary<string, object?>(manifest)
            {
                ["operation_id"] = "<op_id>",
            };
            return JsonSerializer.SerializeToUtf8Bytes(copy, JsonOptions);
        }

        return JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
    }

    private static byte[] SerializePartial(Dictionary<string, object?> partial) =>
        JsonSerializer.SerializeToUtf8Bytes(partial, JsonOptions);

    private void MarkRestored(string manifestPath, string[] destinations, string operationId)
    {
        // Reads, marks item.status=restored, rewrites ATOMICALLY (.tmp → Move).
        // Original fields preserved; history never deleted (ADR-0010 §4.4).
        var bytes = File.ReadAllBytes(manifestPath);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        var rewrittenManifest = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "items":
                    var items = new List<Dictionary<string, object?>>();
                    var idx = 0;
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        var dict = JsonElementToDict(item);
                        // ADR-0010 §4.4: item status → "restored"; path deviation
                        // recorded alongside; original history preserved.
                        dict["status"] = "restored";
                        dict["restored_to"] = destinations[idx];
                        idx++;
                        items.Add(dict);
                    }

                    rewrittenManifest["items"] = items;
                    break;
                case "status":
                    rewrittenManifest["status"] = "restored";
                    break;
                default:
                    // JsonElement.Clone() preserves ValueKind (numbers stay numbers).
                    rewrittenManifest[prop.Name] = prop.Value.Clone();
                    break;
            }
        }

        var tmp = manifestPath + ".restore.tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(rewrittenManifest, JsonOptions));
        // ATOMIC rewrite of the operation's OWN manifest (ADR-0010 §4.4 —
        // "atomically rewriting the manifest; history is never deleted"):
        // the target is our own internal metadata, never user content — the user
        // destination only receives payload after verifying it is FREE.
        File.Move(tmp, manifestPath, overwrite: true);
    }

    private static Dictionary<string, object?> JsonElementToDict(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            // Clone() preserves ValueKind: size stays number, strings stay
            // strings — the rewritten manifest maintains v1 schema types.
            dict[prop.Name] = prop.Value.Clone();
        }

        return dict;
    }

    private static string Message(Exception exception) =>
        $"Move interrupted: {exception.GetType().Name}: {exception.Message}";
}
