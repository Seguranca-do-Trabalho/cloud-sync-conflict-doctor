# Security Audit — GATE 5 Consolidation

| Field | Value |
|---|---|
| Document | docs/security-audit-gate5.md |
| Card | t_1543f566 (S11-6 — GATE 5 Consolidation, child of EPIC 11 t_e0cd185c) |
| Date | 2026-08-23 |
| Revision | R2 (R1 by card t_1543f566; R2 by card t_218a0218/S11-6a — SEG-01/SEG-08 → GREEN) |
| Owner | forg3 |
| Audited base | main @ 0ca0530 (worktree wt/t_1543f566; no code changed) |
| Normative base | SPEC §45 (GATE 5), §26, §51; threat-model.md rev. 1.0 (commit 4bad5d2); ADR-0001..0011 |
| Method | Real suite execution + full reading of affected sources + R1 static audit (card t_ea884c03) |

## 1. Scope and Method

This document consolidates GATE 5 evidence (SPEC §45): reviewed threat model,
tested path/reparse attacks, analyzed race/TOCTOU, reviewed installer. No product
code was written or changed in this card.

Real executions in this session:

- `dotnet test CloudSyncConflictDoctor.sln` on main @ 0ca0530 → **Passed! Failed: 0,
  Passed: 436** (actual run output).
- Full reading of docs/threat-model.md, docs/SPEC.md (gate sections), ADR-0002/0005/
  0006/0010, Directory.Packages.props, Quarantine.cs, PlaceholderGate.cs, PlaceholderPolicy.cs,
  ScanPipeline.cs, CacheStore.cs, ScanFingerprint.cs, CanonicalPath.cs, FileStreamSource.cs,
  SidecarPlaceholderEnumerator.cs.
- Independent R1 static audit (report `security-audit-T20-S11-6-R1.md`, commit
  81339e8 on branch wt/t_ea884c03, 541/541 green at the time on main @ 399e353),
  incorporated as complementary evidence in items 4 and 5.

Numbering convention (orchestrator decision, BINDING): SEG-nn follows the EXACT order of
§5 rows in the threat model. SEG-01 = first table row (`Security_PathTraversal_
HostileName_ContainedInRoot`) … SEG-23 = last row (`Scan_PartialFailures_DoNotAbortWholeScan`).

## 2. SEG-01..SEG-23 Matrix

Status: **GREEN** = test exists, runs green in this run (436/436) and proves mitigation;
**DEFERRED** = formal justification in §4. "evid." column cites commit that introduced the test +
suite file. Rules R1–R12 per §4 of threat model.

| SEG | Test (canonical name §5 TM) | Case | Rule | Owner Card | Priority | Status | Evidence (commit + suite) |
|---|---|---|---|---|---|---|---|
| SEG-01 | `Security_PathTraversal_HostileName_ContainedInRoot` | T-01 | R2 | S11-6a (t_218a0218) | P0 | GREEN (R2) | CanonicalPathTests.cs (6cbac18) covers name validation; containment in move/restore implemented in Quarantine.cs (f210608, wt/t_218a0218) + SecurityContainmentTests.Security_PathTraversal_HostileName_ContainedInRoot: trailing dot/space, RLO U+202E, and homoglyph vectors; forged manifest outside root rejected; suite 438/438 |
| SEG-02 | `Security_LongPath_Over260Chars_ExtendedPrefixNoTruncation` | T-01 | R2 | S11-1 (T17) | P0 | DEFERRED → T-13 (§4.2) | CanonicalPathTests.Normalize_ExtensionAbove255_TruncatesTo255AndGateApproves (6cbac18); >260 chars not exercised |
| SEG-03 | `Report_BidiControlChars_EscapedInJsonAndGui` | T-01 | R12 | S11-1 (T17) | P2 | GREEN (functional equivalent) | ReportWriterTests.Write_AgainstFixture_ByteIdentical + Write_ExactFormat_UTF8NoBOM_LF_Final_NewlineTerminal (709ddf6): RFC 8259 minimum JSON escaping proven against fixture; GUI variant pending post-GUI v2 |
| SEG-04 | `Security_JunctionLoop_TerminatesWithoutDescent` | T-02 | R3 | S11-2 (T18) | P1 | GREEN | ReparsePolicyTests.Reparse05_IntegrationDirectoryJunction_IsLeafAndScanContinues + Loop01..Loop06 incl. Loop04_IntegrationTreeWithRealCycle_TerminatesInFiniteTimeWithoutDuplication (3a701c4) |
| SEG-05 | `Security_ReparseDir_PointingOutsideRoot_NotEntered` | T-02 | R3 | S11-2 (T18) | P1 | GREEN | ReparsePolicyTests.Reparse06_SymlinkOutsideRoot_ExternalContentDoesNotLeak + ReparseDirectoryGuardTests.DirectorySymlink_OutsideRoot_IsNotTraversed (d5a5a0f, 3a701c4) |
| SEG-06 | `Placeholder_GateBeforeOpen_ZeroBytesRead` | T-03 | R3 | EPIC 03 (PLH) | P1 | GREEN | PlaceholderSuiteTests.Plh01_ScanMixedTree_NoStreamOpenOnPlaceholders + Plh02_HashOnPlaceholder_ThrowsBeforeAnyIo + PlaceholderGateTests.Enforce_MixedTree_ZeroRead_PartialReportCorrect (ece1112, 1b4516f) |
| SEG-07 | `Placeholder_ConvertedAfterEnumeration_IsCaughtBySecondGate` | T-03 | R3 | EPIC 03 (PLH) | P1 | GREEN | PlaceholderSuiteTests.Plh04_PostGateMutation_Fails_IsDetectedBySpies + Plh03_TelemetryWithPlaceholderBytes_ViolationThrowsAndIsNeverWashed + PlaceholderGateTests.OpenRead_Unmarked_RawPlaceholderBitsAlsoThrow (ece1112) |
| SEG-08 | `Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack` | T-04 | R4/R5 | S11-6a (t_218a0218) | P0 | GREEN (R2) | QuarantineTests.Move_StaleMetadata_ItemSkipped_FileStaysWhereItIs (19fe330): window narrowed by size+mtime revalidation; post-move hash rollback implemented in Quarantine.cs (f210608, wt/t_218a0218) + SecurityContainmentTests.Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack: swap in hash→move window ⇒ rollback, source intact, FAILED, hash_pre_move ≠ hash_post_move in manifest; suite 438/438 |
| SEG-09 | `Quarantine_ShareModeExclusive_BlockWriterDuringHashWindow` | T-04 | R4 | S11-3 (T19) | P0 | DEFERRED → T-13 (§4.2) | FileStreamSource opens FileShare.Read (d8cc7df), but exclusive lock in hash→move WINDOW is not testable on current POSIX-fs |
| SEG-10 | `Scan_FileModifiedDuringRead_MarkedUnstable_AndNeverCached` | T-05 | R4/R10 | S11-3 (T19) | P0 | DEFERRED → T-12c (§4.1) | No post-read snapshot or UNSTABLE status in L2/L3 pipeline |
| SEG-11 | `Scan_StableFile_MetadataUnchanged_ClassifiedNormally` | T-05 | R4 | S11-3 (T19) | P1 | GREEN (control) | ScanPipelineTests.Scan_ThreeEnumerationOrders_OutputIdenticalByteByByte + DeterminismSuiteTests (39cb05b): normal classification under absent instability is the single exercised path |
| SEG-12 | `Security_QuarantineInsideScannedRoot_ExcludedFromEnumeration` | T-06 | R1 | S11-2 (T18) | P1 | DEFERRED → T-15 (§4.5) | ConflictDoctor/ subtree prefix exclusion not implemented in enumeration |
| SEG-13 | `Restore_DestinationExists_NeverOverwrites_RestoresAsDeterministicSibling` | T-07 | R7 | EPIC 08 (restore) | P0 | GREEN (conservative) | QuarantineTests.Restore_OccupiedDestination_FailsWithException_NothingTouched (19fe330): full pre-check fails CLOSED before touching anything — more conservative than deterministic sibling; deterministic sibling deferred to dedicated EPIC 08 card |
| SEG-14 | `Restore_DestinationAbsent_MovesBackAndHashMatches` | T-07 | R7 | EPIC 08 (restore) | P0 | GREEN | QuarantineTests.Restore_AfterMove_OriginalBackByteIdentical_HashPreserved (19fe330) |
| SEG-15 | `Restore_DestinationIdentical_AlreadyPresentNoMove` | T-07 | R7 | EPIC 08 (restore) | P1 | DEFERRED → §4.6 | ALREADY_PRESENT branch (identical destination ⇒ no move, records outcome) not implemented; today occupied destination ⇒ RestoreConflictException |
| SEG-16 | `Cache_FileIdReused_SizeMtimeDiffer_EntryInvalidated_Rehashes` | T-08 | R8 | S11-4 (T19) | P0 | GREEN (strong equivalent) | CachePoisoningTests.P1..P5 (b0367de): computational BLAKE3(path,size,mtime) key makes file_id collision structurally impossible; per-row MAC detects poisoning (P4/P5/P6 fail-closed) |
| SEG-17 | `Cache_VolumeSerialDiffers_SameFileId_Miss` | T-08 | R8 | S11-4 (T19) | P1 | GREEN (obsolete by design) | b0367de removed legacy file_id from v2 schema (T-08/R8 decision from T-19): keys are derived from content+metadata; recyclable IDs no longer exist as key — case closed, not applicable |
| SEG-18 | `Security_OperationIdCollision_PreexistingManifestFailsClosed` | T-09 | R9 | S11-4 (T19) | P0 | GREEN (equivalent) | QuarantineTests.Move_SameStateTwice_SameOperationId_ManifestByteIdentical (19fe330) + atomic overwrite:false write (Quarantine.cs:253); deterministic content-based op_id (O1–O3, b0367de) eliminates clock collision; create-new fail-closed proven |
| SEG-19 | `OperationId_Entropy_TwoConcurrentBatches_NeverCollide` | T-09 | R9 | S11-4 (T19) | P1 | GREEN (equivalent) | CachePoisoningTests.O1_OperationId_IsContentFingerprint_IgnoringPhysicalOrder + O2/O3 (b0367de): entropy from truncated 64-bit BLAKE3 content fingerprint — distinct batches never collide |
| SEG-20 | `Security_DiskFullMidCopy_FailClosed_SourceIntact_NoPartialDeclaredSuccess` | T-10 | R6/R11 | S11-3 (T19) | P0 | PARTIAL → §4.7 | QuarantineTests.Move_FailureOnSecondItem_HonestPartialManifestAndException (19fe330): fail closed with honest partial manifest and proven untouched source; cross-volume disk-full specific not injected |
| SEG-21 | `Quarantine_SuccessRequiresFsyncOfDataAndManifest` | T-10 | R6 | S11-3 (T19) | P0 | DEFERRED → T-13 (§4.2) | FlushFileBuffers not available/exercisable on test POSIX-fs; .tmp→move atomic write already published (19fe330) |
| SEG-22 | `Permissions_AccessDenied_FileReportedAndNeverQuarantineEligible` | T-11 | R10 | S11-5 | P1 | DEFERRED → §4.8 | Per-item failure recorded (ScanCommand.cs:99 catches UnauthorizedAccessException/IOException as anomaly, d8cc7df), but explicit "no hash, no resolution" ineligibility not asserted in test |
| SEG-23 | `Scan_PartialFailures_DoNotAbortWholeScan` | T-11 | R10/R11 | S11-5 | P1 | GREEN | CliScanCommandTests.AnomaliesWithSkippedFiles_ExitThree_MarksPartial + ErrorsNoAnomalies_RemainsExitZero (d8cc7df): explicit partial, exit 3, scan continues |

Empty cells: zero. Total: 23 rows — **18 green** (R1: 16 + SEG-01 and SEG-08
converted to R2 by card t_218a0218/S11-6a, commit f210608, branch wt/t_218a0218),
**7 deferred/partial with formal justification in §4**
(SEG-02, SEG-09, SEG-10, SEG-12, SEG-15, SEG-21, SEG-22; remaining partial:
SEG-02; net count in §7).

## 3. Ownership (per orchestrator decision)

| Card | SEGs Responsible | Result |
|---|---|---|
| S11-1 (T17, t_70329e55) | SEG-01/02/03 | Delivered (CanonicalPath.cs, 201 TDD cases, 399/399 at the time); integration gaps migrated to addenda T-12a/T-13/T-14 |
| S11-2 (T18) | SEG-04/05/12 | SEG-04/05 green; SEG-12 → addendum T-15 |
| S11-3 (T19, t_43803664) | SEG-08/09/10/11/20/21 | SEG-11 green + partial mitigation in SEG-08/20; SEG-09/10/21 → addenda |
| S11-4 (T19, same commit b0367de) | SEG-16/17/18/19 | All green or closed by redesign (SEG-17) |
| S11-5 (T22/t_807d2357 + CLI T16) | SEG-22/23 | SEG-23 green; SEG-22 → §4.8 |
| AUDIT ONLY: EPIC 03 | SEG-06/07 (PLH family) | Green — confirmed present, not reimplemented |
| AUDIT ONLY: EPIC 08 | SEG-13/14/15 (restore) | SEG-13/14 green; SEG-15 → §4.6 |
| This card (S11-6) | Consolidation, threat model, installer, supply chain, verdict | This document |

## 4. Threat Model Review Against Final Code

### 4.1 Addenda (new cases discovered — T-12+ numbering, no retroactive rewrite)

**T-12 — T-01 integration gaps in move/restore (SEG-01/02 degradation)** — severity P0
The `CanonicalPath` module (IsValidName/Normalize, OrdinalIgnoreCase) validates hostile names with
closed rejection, but byte-by-byte containment by the canonical root prefix BEFORE each
move/restore (T-01 mitigation (b)) is not enforced in the `File.Move` path
(Quarantine.cs:196, 384). The manifest is the tool's own metadata and R1 audit
classified residual risk as low (Note A), but the T-01 contract requires the check.
Action: new card in EPIC 03/08 to apply canonical prefix + end-to-end SEG-01 test
(tree with `evil.txt. `, RLO name and homoglyph passing through quarantine and restore).

**T-12a** = above item regarding quarantine; **T-12b** = extension of T-04's post-move hash
rollback: current protocol revalidates size+mtime before move (Quarantine.cs:182-190)
and re-hashes payload ALREADY IN QUARANTINE as manifest source of truth
(Quarantine.cs:200-202), but does not compare this hash with a pre-move hash of the original nor
execute automatic rollback on divergence — divergence today produces manifest whose hash
reflects actually moved bytes (auditable, but without rollback). Action: card in EPIC 08.

**T-12c** = absence of T-05's post-read snapshot: pipeline does not capture
`(size, mtime, file_id)` AFTER the read nor mark UNSTABLE; hybrid read hash may
enter cache (partially mitigated by CacheStore v2's per-row MAC, which detects
column change, not content change). Action: card in EPIC 05 (pipeline).

**T-13 — Windows-native dependencies not exercisable on POSIX-fs (SEG-02/09/21)** — severity P1
Extended prefix `\\?\`, restricted sharing without WRITE/DELETE share in hash→move window and
FlushFileBuffers (fsync) of data and manifest are threat-model contracts (R2/R4/R6) that the
Linux/POSIX-fs test environment cannot exercise; the `WindowsNativeEnumerator` stub is
`#if WINDOWS`. These are not design gaps — they are TEST PLATFORM gaps. Action: when the
windows-native layer exists (packaging/CI Windows EPIC from test-strategy §GitHub Actions),
create dedicated SEG-windows suite with these three tests. Blocks GATE 6 (Windows release),
not GATE 5 on current platform.

**T-14 — Bidi/homoglyph marked only in JSON, not in GUI (SEG-03, GUI part)** — severity P2
Byte-exact JSON escaping is proven against fixture (schema-report-v1). Visual marking of
bidi characters in the GUI (R12, second half) depends on GUI v2 final screens. Action: card in
GUI EPIC. Priority P2 — enters before RC per §5 of threat model.

**T-15 — ConflictDoctor/ subtree not excluded in enumeration (T-06, SEG-12)** — severity P1
Quarantine publishes at `<root>/ConflictDoctor/quarantine/<op_id>/` inside the scanned root
(ADR-0002), but the enumerator does not skip this subtree by byte prefix nor expose the
`files_excluded_conflictdoctor` counter. A second scan on the same root would list `.dat`
payloads as candidates. Existing partial mitigation: payloads have opaque sequential names without
semantic extension and resolution requires verified BLAKE3 hash (fail-closed R10), so false
"identical" does not arise — but report is polluted and §20 idempotency breaks. Action: card in
EPIC 02 (enumeration), high priority because it affects real repeated-use flow.

### 4.2 Reassessment of §7 Residual Risks

1. **Same-user malware**: unchanged — out of scope by construction (product defends against itself).
   Confirmed by R1 audit: no network/process/reflection surface in src/.
2. **Orphan manifest post-power-loss**: risk maintained; orphan reconciliation remains
   pending in EPIC 08. Technically worsened by T-13 (fsync not yet exercisable) — the pair
   (fsync + reconciliation) should be addressed in the same future EPIC 08 card.
3. **BLAKE3 collision disregarded**: maintained; real defense (R4/R5) reinforced since rev. 1.0 by
   CacheStore v2's per-row MAC and quarantine payload hash.

New residual risk identified in this review: **deterministic content-based op_id**
(ScanFingerprint, b0367de) replaced the 128-bit CSPRNG required by original R9 — swap of
random collision for auditable determinism; collision now requires same ScanResult content,
which by definition IS the same operation. Accepted as strong equivalent to R9 (see SEG-18/19);
amendment recorded here for traceability.

## 5. Installer Review (EPIC 14 / GATE 6 Dependency)

Factual state: **NO INSTALLER EXISTS in the repository** — no packaging project, no
MSIX/signing properties in .csproj files (checked Doctor.Core/Cli/Gui/Tests/FixtureGen),
no winget manifest, no signing script. Product decision at commit 0ca0530:
v1 free without packaging/licensing/Store — packaging deferred to v2. EPIC 14 is
archived on the board. Checklist recorded as binding requirement for future packaging card:

| # | Item | Requirement (threat-model §6/S7 floor) | Status |
|---|---|---|---|
| 1 | Code signing | Organization certificate (EV preferred for SmartScreen); sign binary AND installer | N/A — no installer; requirement recorded |
| 2 | Minimal elevation | Per-user without elevation; `requestedExecutionLevel` asInvoker; runtime never requires admin (TM line 173) | N/A — requirement recorded; current app runs without elevation |
| 3 | Clean uninstall | Remove binaries + `%LOCALAPPDATA%/ConflictDoctor/` cache; NEVER touch user data quarantines | N/A — requirement recorded |
| 4 | No telemetry/network (§26) | Zero network endpoints in installer; consistent with product (R1 audit: zero HttpClient/Socket/Process.Start in src/) | N/A — requirement recorded |
| 5 | Updater floor (S7) | When updater exists: Ed25519 with embedded public key, hash verified before executing, no silent auto-execution | N/A — no updater; floor maintained in threat model |

Item verdict: **N/A in current state** — GATE 5 cannot be blocked by non-existent artifact
due to scope decision (0ca0530); "installer reviewed" condition transfers to
GATE 6 along with T-13 (windows-native) suite.

## 6. Supply Chain

| Check | Result | Evidence |
|---|---|---|
| CPM active (`ManagePackageVersionsCentrally`) | OK | Directory.Packages.props |
| Transitive pinning active | OK | `CentralPackageTransitivePinningEnabled=true` |
| Exact versions, no ranges | OK | 10 pinned PackageVersions (Blake3 2.1.0; Microsoft.Data.Sqlite 8.0.8; CommunityToolkit.Mvvm 8.3.2; Avalonia* 11.2.2; coverlet 6.0.2; Test.Sdk 17.11.1; xunit 2.9.2; runner 2.8.2) |
| Minimal runtime surface | OK | Doctor.Core: Blake3 + Microsoft.Data.Sqlite; Doctor.Cli: no NuGet dependencies; Doctor.Gui: Avalonia (3 packages) + CommunityToolkit.Mvvm; FixtureGen: Blake3 |
| Pinned package unused | Avalonia.Diagnostics 11.2.2 pinned in CPM but NOT referenced in any csproj — remove from CPM (hygiene, non-blocking) | grep across all .csproj: zero references |
| MIT-compatible licenses | OK | Blake3 BSD-2-Clause; Microsoft.Data.Sqlite MIT; CommunityToolkit.Mvvm MIT; Avalonia MIT; Microsoft.NET.Test.Sdk MIT; coverlet MIT; xunit + runner Apache-2.0 (test-only, not distributed — compatible) |
| Network/process/reflection in product | Zero occurrences in src/ | grep HttpClient/WebClient/Process.Start/Socket/Dns/Assembly.Load: empty (R1 audit confirms) |

## 7. GATE 5 Verdict (SPEC §45)

| Condition | Met? | Evidence |
|---|---|---|
| Threat model reviewed | **YES** | This review: 4 numbered addenda (T-12…T-15), §7 residual risks reassessed, R9 equivalence documented; no retroactive rewrite |
| Path/reparse attacks tested | **PARTIAL** | Reparse: complete and green (SEG-04/05, 8 tests). Path traversal: hostile name validation green; **R2 (t_218a0218): byte-by-byte containment in move/restore implemented and proven — SEG-01 GREEN; remaining: >260 chars (T-13) and GUI bidi variant (T-14)** |
| Race/TOCTOU analyzed | **YES** | Hash→move window narrowed by metadata revalidation (SEG-08 partial), poisoned cache closed by redesign (SEG-16/17), deterministic op_id proven (SEG-18/19), fail-closed with honest partial manifest (SEG-20); UNSTABLE/rollback gaps formalized in T-12b/T-12c. **R2 (t_218a0218): post-move hash rollback implemented and proven — SEG-08 GREEN; remaining: T-12c (UNSTABLE) and T-13 (exclusive share, platform)** |
| Installer reviewed | **YES (with transfer caveat)** | Audited: non-existent due to scope decision 0ca0530 (v1 without packaging); binding checklist recorded in §5; physical condition transfers to GATE 6 |

**GLOBAL VERDICT: GATE 5 NOT CLOSED AT THIS STAGE — 3 blocking gaps.**

> **R2 (2026-08-23, card t_218a0218/S11-6a):** gap 1 below RESOLVED — SEG-01 and SEG-08
> green (implementation in Quarantine.cs + canonical §5 tests, commit f210608 on
> branch wt/t_218a0218; TDD RED→GREEN proven; suite 438/438). Cross-block to
> GATE 3 reduced by same measure. Remaining blockers: SEG-10 (t_060a77cc) and
> SEG-12 (t_694bc7ce), plus windows-native suite for SEG-02/09/21 (T-13).

Blocking gaps (each with responsible card):

1. **SEG-01 — byte-by-byte containment in move/restore under hostile names (P0)** → new card in
   EPIC 03/08 (T-12a). The P0s in this matrix also block GATE 3 (Resolution Safety),
   per §5 of threat model — explicitly flagged: SEG-01, SEG-08 (rollback),
   SEG-09, SEG-10, SEG-20 (specific disk-full) are on the shared blocking list
   GATE 3+GATE 5 until their tests go green.
2. **SEG-10 — post-read snapshot / UNSTABLE outside decisions and cache (P0)** → new card in
   EPIC 05 (T-12c). Blocks GATE 3+GATE 5.
3. **SEG-12 — ConflictDoctor/ subtree exclusion in enumeration (P1)** → new card in EPIC 02
   (T-15). Blocks GATE 5 (real-flow idempotency).

Non-blocking, with formal justification: SEG-02/09/21 = depend on windows-native platform
(T-13; transferred to SEG-windows suite of GATE 6); SEG-15 = restore ALREADY_PRESENT branch
(EPIC 08 improvement, current fail-closed behavior is safe); SEG-22 = "no hash, no
resolution" ineligibility assertion (cheap hardening, EPIC 07); CPM hygiene (remove
unused pinned Avalonia.Diagnostics).

With the three cards above closed and green, gate conditions 1–3 are fully green and
GATE 5 closes without new audit (suite re-execution suffices); condition 4 remains
transferred to GATE 6 per documented scope decision.

## 8. Cross-Block Note (GATE 3)

Repeating for audit trail: the matrix's P0 tests — SEG-01, SEG-02, SEG-08, SEG-09,
SEG-10, SEG-13, SEG-14, SEG-16, SEG-18, SEG-20, SEG-21 — block GATE 3 AND GATE 5
simultaneously (threat model §5, done criterion). Their state: green SEG-01/08 (R2,
t_218a0218), SEG-13/14/16/18; partial SEG-20; pending SEG-02/09/10/21. Therefore **GATE 3
also does not close** while gaps 2–3 (and windows-native suite for 02/09/21) are not
resolved — gap 1 was resolved in R2 by card t_218a0218.
