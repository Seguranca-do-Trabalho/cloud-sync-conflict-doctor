# Threat Model — Cloud Sync Conflict Doctor

| Field | Value |
|---|---|
| Document | docs/threat-model.md |
| Card | t_e1fcbbfa (T02 — Threat model, role: security) |
| Date | 2026-08-22 |
| Revision | 1.0 |
| Owner | forg3 |
| Normative base | docs/SPEC.md §2, §3, §6, §12, §18, §22, §26, §45 (GATE 5), §51, §58; ADR-0001, ADR-0002, ADR-0003 |
| Status | Accepted as reference for GATE 5; cases T-01…T-11 are mandatory |

## 1. Scope and Method

This document models what can **destroy user data** in Cloud Sync Conflict Doctor.
The central question is the same as the SPEC's final audit (§58): *can it delete the wrong file,
can it lose a file during quarantine, can it restore to the wrong place, can it corrupt a file?*
Each case below is a concrete path to a "YES" — and the mitigation that turns the YES into a tested NO.

Method: attack surface analysis, then concrete cases with vector, impact, mandatory
mitigation, and the test that proves the mitigation. Decision priority follows §51:
correctness > safety > determinism > data preservation > performance > UX.

Threat model assumptions:

1. The product runs as a regular user, no elevation, no network, no telemetry (§26).
2. The adversary includes: the tree's own content (hostile names), competing sync
   clients (OneDrive/Dropbox etc. mutating files during scan), other local processes
   (including malware running as the same user), and user human error.
3. The Windows filesystem is hostile by default: everything between hash and move can
   change (TOCTOU is the normal state, not the exception).

Assets to protect, in order:

```text
A1. Content and existence of user files (nothing is deleted, corrupted, or overwritten)
A2. File positioning (restore returns to correct place, never replaces new work)
A3. Report correctness (two different versions never classified as identical)
A4. Placeholder economy (no placeholder byte read, no hydration induced)
A5. Manifest integrity (evidence chain from quarantine to restore)
A6. Local-first privacy (no data leaves the machine)
```

## 2. Attack Surfaces

| # | Surface | Data Interaction | Trust Boundary | Main Risk |
|---|---|---|---|---|
| S1 | Scan (Level 0–3) | Metadata and content reading | Outside control: file names, attributes, competing sync clients | Read placeholder (A4); follow reparse outside tree (A6); instability during read corrupting classification (A3) |
| S2 | SQLite Cache | Hash read/write by file_id | Local file modifiable by user/malware; recyclable NTFS IDs | Cache poisoning → false "identical" → quarantine of unique content (A1, A3) |
| S3 | Quarantine (move) | Controlled move/copy+delete | Disk, volumes, ACLs, concurrency | Partial or total loss mid-move; unintended destination from hostile name (A1) |
| S4 | Restore (move back) | Move back to original path | Tree state changed since quarantine | Overwrite new user file (A1, A2) — product's single worst scenario |
| S5 | CLI | Arguments, stdout/JSON | User input and automation/RMM; hostile names leaking into output | Output injection/truncation fooling automation; ambiguous exit code triggering wrong action |
| S6 | GUI | Shows report, triggers resolution | Report content is hostile data (names); user under pressure | Destructive button indistinguishable from safe action (§37); confirmation not showing real path |
| S7 | Updater (future) | Replaces binary | Distribution channel | Malicious/unsigned update = total machine compromise (supply chain) |

Cross-cutting principle: **cache accelerates, never decides alone**. Every decision leading to
quarantine requires a verified evidence chain in the session (current hash or strictly
validated cache tripod — case T-08).

## 3. Concrete Cases

Severity: **P0** = direct path to user data loss/corruption; **P1** = violation of
central invariant (placeholder, privacy, determinism); **P2** = reliable degradation but
no direct loss.

### T-01 — Path traversal via hostile file name — severity P0

| Item | Detail |
|---|---|
| Vector | File name created via `\\?\` API (legal on NTFS, invisible to Explorer): `file.txt.` and `file.txt ` (trailing dot/space — Win32 without extended prefix resolves `file.txt.`, i.e., operates on a DIFFERENT file than displayed); paths >260 chars truncating in ANSI/legacy APIs; hostile Unicode: U+202E (RTL override) making `fdp.exe` visible as `exe.pdf`, Cyrillic/Latin homoglyphs; reserved names (`CON`, `NUL`, `COM1`). |
| Example | Quarantine receives `move("C:\sync\file.txt. ", dest)` built by concatenation without extended prefix → OS moves wrong `file.txt`; report displays RLO-forged name and user approves quarantine of the wrong file thinking it is another. |
| Impact | Operation on different file than intended (A1); user fooled into approval (A3); RMM automation consuming JSON with truncated name acts incorrectly. |
| Mandatory Mitigation | (a) Every path canonicalized once at entry (`GetFullPathName`) and converted to extended form `\\?\`; raw string concatenation of path strings prohibited — always structural combination + re-canonicalization. (b) Byte-by-byte containment check: the resolved path of ANY write/move operation must start with the canonical root prefix (quarantine) — otherwise the operation fails closed. (c) Names preserved exactly as the filesystem gives them (no silent "correction" of dots/spaces/reserved); bidi control characters marked in report. (d) Grouping by `normalized_base_name` operates on exact UTF-8 bytes — no visual fold (homoglyph is a different name). |
| Proving Test | `Security_PathTraversal_HostileName_ContainedInRoot` — tree with `evil.txt. `, RLO name, homoglyph name; quarantine and restore execute and containment prefix validated; no path outside root touched. Companions: `Security_LongPath_Over260Chars_ExtendedPrefixNoTruncation` (>260 chars, move and restore intact) and `Report_BidiControlChars_EscapedInJsonAndGui`. |

### T-02 — Junction/symlink loop in enumeration — severity P1

| Item | Detail |
|---|---|
| Vector | Junction `loop -> .` (self-reference) or cycle `a -> b -> a`; directory symlink pointing outside root (`link -> C:\Users\other`). Naive recursive enumeration enters infinite loop or descends outside the tree. |
| Example | User has legacy junction from migration (`Documents and Settings`); scan enters loop, grows memory/log without end, and in the case of external symlink, hashes of third-party files leak into the local report. |
| Impact | Self-DoS of the scan (never finishes); local-first privacy violation reading outside root (A6); non-deterministic report depending on where the cycle is cut. |
| Mandatory Mitigation | Directory with `FILE_ATTRIBUTE_REPARSE_POINT` is ALWAYS a leaf: records metadata (Level 0), never descends — applies to junction, symlink, mount point, any target (SPEC §6 "don't follow links/reparse"). Defense in depth: visited guard by `(volume_serial, file_id)` and configurable depth cap with report logging. Never decide by visit order (determinism §3). |
| Proving Test | `Security_JunctionLoop_TerminatesWithoutDescent` — synthetic tree with `a->b->a` cycle terminates in finite time and counts reparses as leaves. Companion: `Security_ReparseDir_PointingOutsideRoot_NotEntered` (external content never appears in report). |

### T-03 — Disguised reparse point (placeholder passing the gate) — severity P1

| Item | Detail |
|---|---|
| Vector | OneDrive/Files On-Demand cfapi placeholder whose recall attribute is only visible with correct query; stale metadata in cache; file that was regular and was converted to placeholder between enumeration and opening. A single read induces hydration: gigabytes downloaded, bandwidth cost, sync client behavior change. |
| Example | 8 GB online-only video; gate bug opens the file; `placeholder_bytes_read` goes to 8 GiB and OneDrive downloads everything — exactly the damage the product promises not to cause (A4). |
| Impact | Violation of the `placeholder_bytes_read == 0` invariant (SPEC §6/§21); direct financial/material cost to user; loss of product trust. |
| Mandatory Mitigation | Double gate: (1) attributes from Level 0 enumeration; (2) RE-verification IMMEDIATELY before each open, in the same decision call; any suspicious bit (`OFFLINE`, `RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS`, reparse) → PLACEHOLDER class, zero read. Opening a candidate never uses convenience like `File.ReadAllBytes` on a path — uses handle with explicit flags; `files_placeholder` and `placeholder_bytes_read` counters incremented at open point, and the test asserts zero. |
| Proving Test | `Placeholder_GateBeforeOpen_ZeroBytesRead` — tree with all four SPEC §21 types; hard assertion `placeholder_bytes_read == 0` and no hash function called on placeholder (spy/mock on hashing interface). Companion: `Placeholder_ConvertedAfterEnumeration_IsCaughtBySecondGate`. |

### T-04 — TOCTOU between hash and quarantine move — severity P0

| Item | Detail |
|---|---|
| Vector | Window between hashing the original and completing the move. Another process (user, sync client, malware) swaps content at the path: renames new file into place or rewrites. Manifest records hash H1, but moved bytes are H2. On restore, product returns H2 swearing it is H1 — guaranteed semantic corruption. |
| Example | `report.docx` hashed as good version; Dropbox syncs new version mid-window; quarantine moves new version; future restore puts new version in place of the recorded good — and the "good" is lost. |
| Impact | Data loss/corruption (A1) with appearance of correct procedure — worst failure type for a product whose differentiator is auditable trust. |
| Mandatory Mitigation | Chain evidence, don't trust the window: (1) open candidate with restricted sharing (no `FILE_SHARE_WRITE`, no `FILE_SHARE_DELETE`) during hashing — blocks writers and renamers during critical window; (2) move; (3) **reopen file IN QUARANTINE and recalculate BLAKE3**; (4) compare with pre-move hash; divergence → rollback (move back), operation marked FAILED, nothing declared success. Manifest carries `hash_pre_move` and `hash_post_move` — evidence chain auditable after. Success declared only after (4) matches (case T-10 adds fsync). |
| Proving Test | `Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack` — test hook injects content swap between hash and move; asserts: rollback executed, source intact, operation FAILED in index, `hash_post_move != hash_pre_move` recorded. Companion: `Quarantine_ShareModeExclusive_BlockWriterDuringHashWindow` (second concurrent write fails during window). |

### T-05 — File modified during scan (hash mismatch) — severity P0

| Item | Detail |
|---|---|
| Vector | User edits/saves while scanner reads; sync client replaces file mid-read. Partial/full hash computed on hybrid state (half old, half new). |
| Example | Spreadsheet saved at 19:00:01; read started 19:00:00; resulting BLAKE3 does not correspond to any real version. If this hash enters cache (S2), the poison persists in following scans (combines with T-08). |
| Impact | Wrong classification: two different versions may appear identical (A3) → quarantine resolution of unique content (A1); or identical appears divergent (noise, less severe). |
| Mandatory Mitigation | Per-file consistency snapshot: capture `(size, mtime, file_id)` BEFORE the read, recheck all three AFTER; any change → file marked `UNSTABLE`, excluded from all equality/divergence decisions in this session (re-queued once; persisting, enters report as unstable). Unstable file hash is NEVER written to cache. Fail-closed rule: doubt about equality ⇒ treat as potential divergence, never as safe copy to remove. |
| Proving Test | `Scan_FileModifiedDuringRead_MarkedUnstable_AndNeverCached` — simulated concurrent write mid-read; asserts: UNSTABLE status, outside identical groups, no cache entry for file. Companion: `Scan_StableFile_MetadataUnchanged_ClassifiedNormally` (positive control). |

### T-06 — Quarantine inside scanned tree (recursion/self-ingestion) — severity P1

| Item | Detail |
|---|---|
| Vector | ADR-0002 places quarantine at `<root>/ConflictDoctor/quarantine/<timestamp>/` — inside the synced folder. Next scan enumerates quarantined copies as live files: duplicate appears double, and resolution may quarantine content ALREADY IN quarantine, piling trash and polluting manifests. Sync client still propagates `ConflictDoctor/` to other machines, multiplying the problem. |
| Example | Scan of `C:\Users\me\OneDrive`; resolution moves 500 duplicates; new scan finds 1000 candidates (500 live + 500 quarantined); user resolves again; 500 phantom copies become permanent candidates. |
| Impact | Incorrect and non-idempotent report (A3); risk of redundant operations on already-protected content; infinite phantom candidate growth. |
| Mandatory Mitigation | Structural exclusion in enumeration: `<root>/ConflictDoctor/` subtree is skipped at Level 0 by BYTE PREFIX comparison of the canonical path (cheap, deterministic), with dedicated counter `files_excluded_conflictdoctor` in telemetry. Second layer: quarantine marks its files (HIDDEN attribute + index entry) and scanner ignores any candidate present in the quarantine index. Third layer: resolution refuses to move a file whose path is already under the quarantine directory. |
| Proving Test | `Security_QuarantineInsideScannedRoot_ExcludedFromEnumeration` — resolve in a tree, scan again; asserts: zero `ConflictDoctor/` items in report, `files_excluded_conflictdoctor > 0`, second scan byte-identical to first (idempotency §20). |

### T-07 — Restore overwriting new file — severity P0

| Item | Detail |
|---|---|
| Vector | Between quarantine and restore, a file appears at the original path: user recreated it, sync client brought version from another machine, another tool wrote there. Naive restore (`move` over) DESTROYS the new file — permanent loss, no quarantine, no undo. It is the product's only operation that can delete data without going through the quarantine path. |
| Example | Conflicted `budget.xlsx` goes to quarantine; sync client restores server version at the same path next day; user clicks "restore"; server version overwritten by quarantined one and disappears. |
| Impact | Irreversible destruction of user work (A1, A2) — violates ADR-0002 §3 ("never overwrite") and the product's core promise. |
| Mandatory Mitigation | Restore NEVER writes over existing destination, no exception: (1) absent destination → move back, verify hash, record SUCCESS; (2) existing destination with identical hash to manifest → does not touch existing, records `ALREADY_PRESENT`, does not move; (3) existing destination with different content → moves quarantined file to deterministic sibling path `base (restored <short_operation_id>).ext` (never replaces, never random suffix) OR fails requesting explicit decision — conservative default: sibling + warning. In all cases the original manifest gains an append-only outcome record. Comparison always by BLAKE3 hash, never by mtime/size alone. |
| Proving Test | `Restore_DestinationExists_NeverOverwrites_RestoresAsDeterministicSibling` — destination occupied by different content; asserts: existing untouched byte-by-byte, quarantined present at deterministic sibling, outcome recorded. Companions: `Restore_DestinationAbsent_MovesBackAndHashMatches` (happy path) and `Restore_DestinationIdentical_AlreadyPresentNoMove`. |

### T-08 — Cache poisoned by file ID reuse — severity P0

| Item | Detail |
|---|---|
| Vector | NTFS reuses file IDs after deletion. Cache keyed only by `file_id` (SPEC §12) returns the OLD file's hash for a NEW file that inherited the ID. Two different contents now share hash "in cache" → classified as identical copies. |
| Example | `contract_v1.docx` deleted; ID recycled by `contract_FINAL.docx` with different content; cache says hash same as `contract_copy.docx`; doctor declares "identical copies"; user keeps one and quarantines the other — unique content destroyed with product approval. |
| Impact | False "identical" is cheapest path to data destruction (A1, A3) and survives process restarts (poison persistence in SQLite). |
| Mandatory Mitigation | Composite validity key: cache entry used only if `(volume_serial, file_id, size, mtime_ticks, algorithm, hash_version)` ALL match exactly current file state; any divergence = cache miss → full pipeline rehash. `mtime` at native max precision (ticks, not seconds). `volume_serial` accompanies every file ID (IDs only unique per volume). Reinforcement: cache hash used in resolution decision generates its own audit line in report (`evidence: cache` vs `evidence: fresh`). Cache is never sole authority (§2 principle). |
| Proving Test | `Cache_FileIdReused_SizeMtimeDiffer_EntryInvalidated_Rehashes` — fixture simulates ID reuse with different size/mtime; asserts: rehash executed, old hash not reused, correct classification. Companion: `Cache_VolumeSerialDiffers_SameFileId_Miss` (same ID on different volumes does not collide). |

### T-09 — operation_id collision — severity P0

| Item | Detail |
|---|---|
| Vector | Two batches in the same second (scheduled + manual) generate the same `operation_id` derived from timestamp; second manifest OVERWRITES the first. First batch loses its evidence chain: restore no longer knows which bytes correspond to which entry — ambiguous restoration on user data. |
| Example | 01h00m00s batch quarantines 300 files; user triggers another batch in the same second; first manifest replaced; bulk restore from previous day restores crossed entries. |
| Impact | Evidence chain corruption (A5) → wrong restore (A1, A2); audit impossible (violates §2.1 item 7 — reason/tracking). |
| Mandatory Mitigation | `operation_id` = 128-bit CSPRNG (hex/ULID format), clock-independent; manifest filename incorporates the id; manifest write is create-new: existing ⇒ FAILED operation (never overwrite, never append to another batch's manifest). Operation index is append-only. Wall clock enters only as informational `timestamp` field, never as key. |
| Proving Test | `Security_OperationIdCollision_PreexistingManifestFailsClosed` — forces duplicate ID (injection); asserts: second operation fails without touching existing manifest, first remains intact, operational error exit code. Companion: `OperationId_Entropy_TwoConcurrentBatches_NeverCollide`. |

### T-10 — Disk full mid-move — severity P0

| Item | Detail |
|---|---|
| Vector | Cross-volume move degrades to copy+delete. Quarantine disk fills mid-copy → fragment in quarantine; naive implementation already deleted source, or declares success with truncated destination. Result: half the bytes on each side. Even in same-volume rename, lack of fsync allows logical success on non-persisted state after power loss. |
| Example | 40 GB batch to nearly full external HD; copy dies at 70%; source removed "because it's a move"; user loses 30% of bytes from each file in the batch. |
| Impact | Silent partial loss (A1) — perhaps worse than total loss because it goes unnoticed until restore. |
| Mandatory Mitigation | Safe move protocol, step by step: (1) copy to `<dest>.<op-partial>` temp name; (2) `FlushFileBuffers` (fsync) on temp; (3) reopen and re-hash temp (BLAKE3, also covers T-04); (4) hash matches → atomic rename temp→final; (5) ONLY THEN release source (delete temp-source in cross-volume case; in same-volume rename is already atomic and step becomes existence check); (6) write manifest + fsync manifest; (7) declare success. Any step fails → remove temp, source INTACT, operation FAILED in index, exit code 1. Pre-check of estimated free space before batch (rejects impossible batch before touching any file). |
| Proving Test | `Security_DiskFullMidCopy_FailClosed_SourceIntact_NoPartialDeclaredSuccess` — small quota/tempfs or injected failure at step 2; asserts: source present byte-by-byte, temp removed, index marks FAILED, exit code 1. Companion: `Quarantine_SuccessRequiresFsyncOfDataAndManifest` (assert flush call before success recording). |

### T-11 — NTFS permissions denied — severity P2

| Item | Detail |
|---|---|
| Vector | Individual file DENY read ACL (or broken inheritance in subtree); enumeration sees name, open fails with `ERROR_ACCESS_DENIED`. Also: quarantine on volume/folder with ACL denying write to user. |
| Example | `financial` folder with restrictive ACL inside scanned tree; scan cannot hash 12 files; resolution batch proceeds without them and report is silent — user believes the entire folder was analyzed. |
| Impact | Silently incomplete = dishonest analysis (A3); worst variant: file without hash eligible for resolution because "it looked like a duplicate by name+size". |
| Mandatory Mitigation | Explicit per-item failure: access error becomes `ACCESS_DENIED` status in report (with counters), never aborts entire scan nor is swallowed. HARD RULE: file without verified BLAKE3 hash is NEVER eligible for resolution/quarantine — no evidence, no removal (fail-closed, consistent with §51 priorities). Report summary displays "X files not analyzed due to denied access" on first screen (§15). Product never requires elevation to complete: what the user cannot read, the product does not decide. Exit code 3 (partial) when unanalyzed items exist. |
| Proving Test | `Permissions_AccessDenied_FileReportedAndNeverQuarantineEligible` — simulated ACL deny (chmod on test POSIX-fs / FS abstraction mock); asserts: ACCESS_DENIED status in report, file outside resolution candidates, exit 3 in partial mode. Companion: `Scan_PartialFailures_DoNotAbortWholeScan` (remaining files analyzed normally). |

## 4. Derived Rules (mandatory for all product code)

These rules are binding requirements extracted from the cases above. Implementation
cards must cite them; the reviewer enforces each at GATE 5.

```text
R1  (T-06)  Quarantine outside scanned root OR, if inside, ConflictDoctor/ subtree
            excluded from enumeration by byte prefix, with dedicated counter.

R2  (T-01)  No path handled as raw string: single canonicalization at entry +
            extended form \\?\ + byte-by-byte containment check before every
            write/move operation. Ad-hoc path concatenation prohibited.

R3  (T-02/T-03) Reparse point never descended or followed; directory with reparse is leaf;
            double attribute gate (enumeration + immediate re-check before open).

R4  (T-04/T-05) Hash only via handle with restricted sharing (no WRITE/DELETE share);
            metadata (size, mtime, file_id) captured before and checked after
            read; divergence => UNSTABLE, outside decisions, outside cache.

R5  (T-04/T-10) Post-move verification by hash (BLAKE3 recalculated at destination) before
            declaring success; manifest records hash_pre_move and hash_post_move.

R6  (T-10)  fsync (FlushFileBuffers) of data AND manifest before any success;
            safe move protocol: temp → flush → rehash → atomic rename → release
            source → manifest → success. Failure at any step: source intact, FAILED.

R7  (T-07)  Restore never overwrites: existing destination with different content goes to
            deterministic sibling "(restored <op>)" or fails requesting decision; comparison
            by hash, never by mtime/size alone.

R8  (T-08)  Cache valid only with (volume_serial, file_id, size, mtime_ticks,
            algorithm, hash_version) all equal; divergence => rehash; cache accelerates,
            never decides alone; cache use in decision generates audit trail.

R9  (T-09)  operation_id from 128-bit CSPRNG; manifest is create-new; existing => fail
            closed; operation index append-only; timestamp never a key.

R10 (T-05/T-11) File not hashable (ACL denied, UNSTABLE, unavailable) is never eligible
            for resolution/quarantine; per-item failures appear in report and summary
            on first screen; partial scan => own exit code.

R11 (all) Failure at destructive step terminates process conservatively (ADR-0002
            §4); nothing partial remains unrecorded in operation index.

R12 (T-01/S5) Strict name escaping in JSON output and GUI; bidi characters marked;
            grouping by exact UTF-8 bytes, no visual/locale fold.
```

## 5. Required Tests (consolidated for GATE 5)

Canonical names in `tests/Doctor.Tests`. Corresponding implementation cards must
create exactly these tests (or superset with traceability to this document).

| Test | Proves | Case | Priority |
|---|---|---|---|
| `Security_PathTraversal_HostileName_ContainedInRoot` | Byte-by-byte containment under hostile names | T-01 | P0 |
| `Security_LongPath_Over260Chars_ExtendedPrefixNoTruncation` | No truncation above 260 chars | T-01 | P0 |
| `Report_BidiControlChars_EscapedInJsonAndGui` | RLO name does not fool output | T-01 | P2 |
| `Security_JunctionLoop_TerminatesWithoutDescent` | Cycle terminates; reparse is leaf | T-02 | P1 |
| `Security_ReparseDir_PointingOutsideRoot_NotEntered` | Nothing outside root is read | T-02 | P1 |
| `Placeholder_GateBeforeOpen_ZeroBytesRead` | `placeholder_bytes_read == 0` (§21) | T-03 | P1 |
| `Placeholder_ConvertedAfterEnumeration_IsCaughtBySecondGate` | Double gate works | T-03 | P1 |
| `Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack` | Rollback by post-move hash | T-04 | P0 |
| `Quarantine_ShareModeExclusive_BlockWriterDuringHashWindow` | TOCTOU window minimized | T-04 | P0 |
| `Scan_FileModifiedDuringRead_MarkedUnstable_AndNeverCached` | Unstable does not classify or poison cache | T-05 | P0 |
| `Scan_StableFile_MetadataUnchanged_ClassifiedNormally` | Snapshot positive control | T-05 | P1 |
| `Security_QuarantineInsideScannedRoot_ExcludedFromEnumeration` | Scan idempotency post-resolution | T-06 | P1 |
| `Restore_DestinationExists_NeverOverwrites_RestoresAsDeterministicSibling` | Never overwrites | T-07 | P0 |
| `Restore_DestinationAbsent_MovesBackAndHashMatches` | Happy path restore | T-07 | P0 |
| `Restore_DestinationIdentical_AlreadyPresentNoMove` | Restore idempotency | T-07 | P1 |
| `Cache_FileIdReused_SizeMtimeDiffer_EntryInvalidated_Rehashes` | ID reuse does not poison | T-08 | P0 |
| `Cache_VolumeSerialDiffers_SameFileId_Miss` | Per-volume IDs do not collide | T-08 | P1 |
| `Security_OperationIdCollision_PreexistingManifestFailsClosed` | Manifest never overwritten | T-09 | P0 |
| `OperationId_Entropy_TwoConcurrentBatches_NeverCollide` | ID entropy | T-09 | P1 |
| `Security_DiskFullMidCopy_FailClosed_SourceIntact_NoPartialDeclaredSuccess` | Fail closed without loss | T-10 | P0 |
| `Quarantine_SuccessRequiresFsyncOfDataAndManifest` | Success only after flush | T-10 | P0 |
| `Permissions_AccessDenied_FileReportedAndNeverQuarantineEligible` | No hash, no resolution | T-11 | P1 |
| `Scan_PartialFailures_DoNotAbortWholeScan` | Partial is explicit, not silent | T-11 | P1 |

Section done criteria: P0 tests block GATE 3 (Resolution Safety) and GATE 5
(Security); P1 tests block GATE 5; P2 tests enter before RC.

## 6. Notes per Remaining Surface (CLI, GUI, Updater)

- **S5 CLI:** no shell invocation; arguments parsed, never interpreted; JSON with strict
  escaping (R12) and `report_schema_version` always on first logical line; stable exit codes
  per CLI design (0/1/2/3 from SPEC §14) — RMM automation must never infer success from
  a partial scan. Coverage: exit code contract tests belong to EPIC 09; this
  document fixes the requirement that exit 3 be emitted in any scan with unanalyzed items.
- **S6 GUI:** no engine logic (ADR-0003); visually unambiguous destructive actions (§37);
  confirmation dialog shows source path, quarantine path, and hash — user approves
  evidence, not a summary. Hostile names escaped (R12). GUI never offers "delete" — only
  "move to quarantine" (ADR-0002 consequences).
- **S7 Updater (future):** supply chain threat — update replaces binary and compromises everything
  this model guarantees. Minimum requirements when it exists: Ed25519 signed manifest with
  embedded public key, binary hash verified before execution, no silent
  auto-execution; Microsoft Store channel already provides signed distribution.
  Detailed decision belongs to the updater ADR (EPIC 14); this document fixes the
  security floor.

## 7. Assumed Residual Risks

1. Malware running as the same user can do anything to files — the product does not
   defend against an adversary with user privileges; it defends against itself failing.
2. Power loss between atomic rename and manifest fsync may leave quarantined file
   without corresponding manifest; mitigation: reconciliation routine (orphan scan on
   startup, orphan entries stay quarantined, never auto-restored) — future card
   in EPIC 08.
3. BLAKE3 collision is disregarded (brute force impractical); real defense against wrong hash is
   the metadata snapshot (R4) and post-move hash (R5), which cover plausible causes.
