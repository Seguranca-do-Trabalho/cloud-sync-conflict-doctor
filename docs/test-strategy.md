# Test Strategy — Cloud Sync Conflict Doctor (T05)

| | |
|---|---|
| **Card** | t_4958fec7 — T05 Test strategy (role: qa) |
| **Date** | 2026-08-22 |
| **Revision** | 1 |
| **Owner** | forg3 |
| **Scope** | Strategy document. No product code created in this card. |

Sources read in full in this session before any citation: `docs/SPEC.md`
(1822 lines), `docs/adr/ADR-0001.md`, `ADR-0002.md`, `ADR-0003.md`,
`README.md` and `docs/recognition.md` (branch `wt/t_76be6462`). The interface
contracts cited (`IFileEnumerator`, `IHasher`, etc.) are those defined by sibling
card T01 in `docs/contracts.md`; where this document depends on exact
signature, it is marked as an alignment condition in section 9.

Reading convention: each mandatory test has a **stable ID**, **proposed xUnit
method name**, **level** (unit / integration / native-windows / bench),
**SPEC reference** and verifiable **acceptance criteria**. Green suite = all
tests in the family passing on the required operating system(s).

---

## 1. Test Pyramid

Bottom to top. Volume decreases, cost and fidelity increase.

```text
        ┌─────────────────────────────────────────────┐
        │ BENCHMARK (§23–24)                          │  smoke 500 in nightly CI;
        │ generated datasets, metrics, regression     │  1M files outside the PR
        ├─────────────────────────────────────────────┤
        │ DETERMINISM (§20, ADR-0003)                │  3+ scans, injected orders,
        │ byte-by-byte, runs on BOTH OS in CI        │  output compared byte by byte
        ├─────────────────────────────────────────────┤
        │ SAFETY (§22, ADR-0002, threat model T02)   │  non-destruction, quarantine,
        │                                             │  restore, static guard
        ├─────────────────────────────────────────────┤
        │ INTEGRATION                                │  real synthetic trees in
        │ (filesystem tmp)                            │  temp directory, full pipeline
        │                                             │  end-to-end CLI
        ├─────────────────────────────────────────────┤
        │ UNIT (xUnit)                                │  byte comparator, tie-break,
        │                                             │  normalization, schema, GUI VMs
        └─────────────────────────────────────────────┘
```

Rules governing all layers:

1. **TDD (§19, §46).** Critical features start with a failing test. No
   `Doctor.Core` class enters main without a test exercising it.
2. **Determinism is tested, not declared (§3, §20).** Every output test
   compares bytes, never "same logical content".
3. **Priority (§51)** correctness > safety > determinism > data preservation >
   performance > UX guides which suite blocks which gate (section 8).
4. **Anti-overengineering (§52).** No additional test framework beyond
   xUnit + coverlet; no heavy mock library — the fakes (fake/spy) are
   hand-written against the T01 contracts, because they are also executable
   specifications of those contracts.

### 1.1 xUnit Categories (traits)

| Trait | Value | Usage |
|---|---|---|
| `Category` | `Determinism`, `Placeholder`, `Safety`, `Cache`, `Report`, `Grouping`, `Hashing`, `Cli`, `GuiVm`, `Security`, `Benchmark` | selection by family and by gate |
| `OS` | `Windows` | native Windows tests; excluded from Linux job via filter |

Canonical filters:

```bash
# Linux job (and any Unix dev):
dotnet test --filter "OS!=Windows"
# everything with Trait "OS=Windows" (native) stays out; other categories run

# Windows job:
dotnet test  # complete, including native-windows
```

---

## 2. Test Seams — Design Decisions

The product is only testable at the points below if the T01 contracts reserve
these seams. They are test strategy requirements on the contracts:

### 2.1 Order Injection — `IFileEnumerator`

Contract: `IFileEnumerator.Enumerate(root)` produces Level 0 entries in
**unspecified order**. The pipeline is required to sort internally (UTF-8 bytes
of path) before any grouping, decision, or output. Thus the enumeration
order can be injected without touching the pipeline:

```csharp
// Test implementations over the SAME production contract:
sealed class ReversedFileEnumerator(IFileEnumerator inner) : IFileEnumerator
    => Enumerate() = inner.Enumerate().Reverse();            // order #2

sealed class ShuffledFileEnumerator(IFileEnumerator inner, int seed) : IFileEnumerator
    // Fisher-Yates with shareable Random from FIXED seed declared in the test.
    // Never Random without seed: the test must be reproducible on failure.
```

Scan A = real enumerator (filesystem order); scan B = reversed; scan C =
shuffled (fixed seed). Outputs compared byte by byte (§20).

### 2.2 Non-Opening Proof — Spy `IHasher` and Stream Factory

```csharp
sealed class CountingHasher : IHasher
{
    List<string> PartialCalls, FullCalls;   // records EVERY path received
    int OpenedStreams;                      // instrumented stream factory
}
```

A placeholder proven untouched requires TWO independent evidences (§21):

1. no hash call whose path is a placeholder;
2. no `Stream` opened on a placeholder (open counter) and
   `placeholder_bytes_read == 0` in the report telemetry.

### 2.3 Frozen Time — `TimeProvider`

Byte-by-byte comparison between scans is only possible if `generated_from`
(`scan_started_utc`, `scan_finished_utc`) is identical. The reporter receives
`TimeProvider` (.NET 8 default); tests inject a frozen `FakeTimeProvider`.
Without this seam, §20 is impossible to satisfy literally — therefore it is
**a condition on T03**: the only time present in the report comes from the
injected `TimeProvider`.

### 2.4 Paths Relative to Scanned Root

For the golden file to run identically on Ubuntu and Windows (test DET-06,
question Q12 of §58), the report serializes paths **relative to the scanned
root**, with `/` normalized separator. **Condition on T03** (schema v1):
without this, the same dataset produces different bytes across machines due to
the absolute prefix.

### 2.5 Quarantine with Injectable Root — `IQuarantine`

The quarantine root (`<root>/ConflictDoctor/quarantine/<timestamp>/`) is
received via parameter/injection, never resolved globally. Allows testing in
temp directories and allows the T02 "quarantine inside the tree" vector to be
truly tested (excluded from enumeration).

---

## 3. SPEC → Mandatory Test Map

IDs are stable and citable by other cards (threat model T02 references the
`SEG-*` namespace; this document does not invent names within it — section 3.9).

### 3.1 Determinism — DET Family (§20; ADR-0003; GATE 2)

| ID | Proposed Name (xUnit) | Level | Acceptance Criteria |
|---|---|---|---|
| DET-01 | `Scan_ThreeEnumerationOrders_ReportsByteIdentical` | integration | FX-MIN tree; scans with real enumerator, reversed, and shuffled (seed 42), frozen `TimeProvider`. The 3 JSONs have identical bytes (byte-by-byte comparison). Runs on both OS. |
| DET-02 | `Scan_TenFixedShuffleSeeds_AllReportsByteIdentical` | integration | 10 fixed seeds declared in test code (no randomness in test). All outputs identical to DET-01. |
| DET-03 | `ParallelHashing_ThreadCompletionOrder_ReportBytesIdentical` | integration | Pipeline with active parallel hash pool + shuffled enumerator; output identical to DET-01's serial scan. Proves decision does not depend on thread order (§11, §20). |
| DET-04 | `PathOrdering_UsesUtf8ByteOrder_RegardlessOfCulture` | unit | `PathByteComparer` on trap names (`Zebra.txt`, `apple.txt`, `Apple.txt`, `Árvore.txt`, `zebra.txt`) with `CurrentCulture` forced to `tr-TR` then `pt-BR`. Resulting order == expected UTF-8 byte order, explicit in test, in both cultures. |
| DET-05 | `TieBreak_MtimeThenSizeThenPath_NeverFirstSeen` | unit | Rule resolver (KEEP_NEWEST and others, §17) with constructed ties: equal mtime → decides by size; equal size → decides by path bytes; entries presented in reversed orders always produce the same winner. Never "first seen". |
| DET-06 | `GoldenReport_FixtureMin_MatchesCommittedGoldenBytes` | integration | FX-GOLDEN (fixed versioned tree) → report compared to committed golden file. Passes on Ubuntu job AND Windows (depends on conditions 2.3 and 2.4). Closes question Q12 of §58 for what is automatable. |

### 3.2 Placeholder — PLH Family (§6, §21; GATE 2)

| ID | Proposed Name (xUnit) | Level | Acceptance Criteria |
|---|---|---|---|
| PLH-01 | `PlaceholderEntries_NeverHashed_NeverOpened_ZeroBytesRead` | unit | `FakeFileEnumerator` returns mixture of normal and placeholder entries with OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS / reparse attributes. With `CountingHasher`: zero hash calls on placeholder paths, zero streams opened on placeholders, telemetry `placeholder_bytes_read == 0`, correct `files_placeholder`. |
| PLH-02 | `MixedTree_NormalAndSidecarPlaceholders_PipelineCompletesZeroPlaceholderBytes` | integration | FX-PLACEHOLDER tree in tmp (Linux: placeholders simulated via `.placeholder-meta.json` sidecar, T04 convention). Scan completes; `placeholder_bytes_read == 0`; placeholder content intact byte by byte. |
| PLH-03 | `WindowsNative_RealOfflineRecallAndReparseAttributes_ZeroBytesRead` | native-windows | On real Windows: `FILE_ATTRIBUTE_OFFLINE` set via P/Invoke, junction, symlink, ROO/RODA when FS allows. Same criteria as PLH-01 on the production enumerator (FindFirstFileEx). This test is the reason the Windows job exists. |
| PLH-04 | `ReparseLoop_JunctionOrSymlinkCycle_TerminatesWithoutTraversing` | integration (+native-windows variant) | Junction cycle (Win) / symlink cycle (Linux) inside the tree. Enumeration terminates in finite time, does not traverse the cycle twice, nothing from the cycle is read (`bytes_read` of cycle == 0). Threat model T02 vector. |

### 3.3 Non-Destruction and Quarantine — NDES/QRT Families (§22; ADR-0002; GATE 3)

| ID | Proposed Name (xUnit) | Level | Acceptance Criteria |
|---|---|---|---|
| NDES-01 | `Resolution_KeepNewest_OriginalMoved_QuarantineAndManifestExist_HashMatches` | integration | After resolution: file removed from original path, exists in dated quarantine, JSON manifest exists with all ADR-0002 fields, manifest hash == recalculated BLAKE3 of moved file, mtime/size preserved. |
| NDES-02 | `Restore_AfterQuarantine_FileBackByteIdentical_HashVerified` | integration | Restore puts file back at original path; content byte-identical to pre-move; hash verified; restore record in operation history. |
| NDES-03 | `Restore_TargetPathOccupied_NeverOverwrites_ConservativeFailure` | integration | New file occupies the original path. Restore DOES NOT overwrite (ADR-0002 item 3): conservative failure or logged diversion, occupant intact, quarantine intact. |
| NDES-04 | `MoveToFails_MidOperation_NoUnrecordedPartialState` | integration | Injected mover fails after starting (simulates full disk/permission). Consistent state: either complete move with manifest, or nothing moved; process terminates conservatively; nothing partial unrecorded (ADR-0002 item 4). |
| NDES-05 | `SourceGuard_NoDeleteApisInProductCode` | unit (static guard) | Scans `src/**/*.cs` text: `File.Delete`, `Directory.Delete`, `DeleteFile`/`DeleteFileW` (P/Invoke), `FILE_DISPOSITION_INFO`/`SetFileInformationByHandle(FileDispositionInfo*)`, `FILE_RENAME_INFO*` outside quarantine move are prohibited. Empty allowlist in v1. Zero occurrences = pass. Complements the reviewer's static review (ADR-0002 item 5). |
| QRT-06 | `Manifest_CompleteDeterministicFields_NoIncidentalTimeInLists` | unit | Repeated operation manifests on the same tree have identical entries except `operation_id`/header timestamp; fields exactly match ADR-0002; sorted by path in bytes. |

### 3.4 Incremental Cache — CACHE Family (§12)

| ID | Proposed Name (xUnit) | Level | Acceptance Criteria |
|---|---|---|---|
| CACHE-01 | `RenameFile_Rescan_CacheReused_NoFullHashRerun` | integration | Rename file (same file ID/inode), rescan with `CountingHasher`: zero new full hashes for it; group/classification identical. Logical key by file ID, not path. |
| CACHE-02 | `ContentChanged_SameSize_NewMtime_CacheInvalidated` | integration | Content swapped keeping size, mtime advances: rescan recalculates (partial/full per pipeline) and classifies correctly as divergence. |
| CACHE-03 | `CacheEntry_AlgorithmOrHashVersionMismatch_Recalculated` | unit/integration | Cache entry with `algorithm`/`hash_version` different from current is ignored and recalculated; never reused. |
| CACHE-04 | `PoisonedCache_ReusedFileId_SizeOrMtimeMismatch_Recalculated` | integration | T02 vector: poisoned cache row (file ID reused with size/mtime divergent from disk). Cache cannot mask real content: mandatory recalculation when metadata does not match. |

### 3.5 Report — RPT Family (§13; ADR-0003; T03 Schema)

| ID | Proposed Name (xUnit) | Level | Acceptance Criteria |
|---|---|---|---|
| RPT-01 | `Report_ConformsToSchemaV1_TypesRequiredFieldsPresent` | unit | FX-MIN report validates field by field against `docs/schema-report-v1.md`; T03 fixture `fixtures/report-v1-example.json` used as form reference. |
| RPT-02 | `Report_ListsSortedByPathBytes_NoIncidentalTimestampInsideLists` | unit | Every JSON list in UTF-8 byte path order; no time fields inside list items (ADR-0003 rule 3). |
| RPT-03 | `Report_Blake3Explicit_AndVersionsPresent` | unit | `algorithm == "BLAKE3"`, `hash_version == 1`, `report_schema_version == 1` present (§4). Future algorithm swap without bump = test keeps catching. |

### 3.6 Grouping and Hashing — GRP/HSH Families (§7–§9)

| ID | Proposed Name (xUnit) | Level | Acceptance Criteria |
|---|---|---|---|
| GRP-01 | `Normalization_KnownSuffixList_BaseNameExtracted` | unit | Fixed versioned pattern list (§7): ` (1)`, ` (2)`, ` (conflicted copy)`, `-DESKTOP-XXXX`, `~`, `~$`, `.sb-<hex>`. Each pattern with positive and negative case (name without suffix is not altered). |
| GRP-02 | `Normalization_UnknownSuffix_LeftAlone_LimitedNotInfinite` | unit | Suffix outside the list is not normalized (no infinite heuristic); normalization version recorded in report. |
| GRP-03 | `Grouping_NormalizedNamePlusSize_OnlyEqualSizesGrouped` | unit/integration | Candidates grouped by normalized base + size; different sizes never group. |
| HSH-01 | `PartialHash_ReadsOnlyFirst64KiBAndLast64KiB` | unit | Spy stream on 1 MiB file: bytes read ≤ 128 KiB, start+end pattern (§8). |
| HSH-02 | `FullHash_ExecutedOnlyForPartialSurvivors` | integration | Tree with same-size/different-content pairs: `CountingHasher` proves that those dying at Level 2 never had full hash (§8, §9, §10). |
| HSH-03 | `IdenticalContent_FullHashEqual_ClassifiedIdenticalDuplicate` | integration | Byte-identical copies → `identical_duplicates`. |
| HSH-04 | `DifferentContent_SameNormalizedBase_ClassifiedRealConflict` | integration | Same normalized name, different content → `real_conflicts`; real conflict never appears as duplicate (Q9/Q10 of §58). |

### 3.7 CLI — CLI Family (§14)

| ID | Proposed Name (xUnit) | Level | Acceptance Criteria |
|---|---|---|---|
| CLI-01 | `Scan_CleanTree_Exit0_QuietNoStdout` | integration | Tree without anomalies → exit 0; `--quiet` with no stdout output. |
| CLI-02 | `Scan_TreeWithConflicts_Exit2_JsonValidAgainstSchema` | integration | Tree with duplicates/conflicts → exit 2; `--json` emits valid report (RPT-01). |
| CLI-03 | `Scan_OperationalError_Exit1` | integration | Nonexistent/unreadable root → exit 1, stderr message. |
| CLI-04 | `Scan_PartialCompletion_Exit3` | integration | Injected partial failure during scan (unreadable subtree) → exit 3 and report marks incompleteness. |

Formal exit codes 0/1/2/3 belong to ADR-0008 (T01); this suite locks them.

### 3.8 GUI (ViewModel) and Benchmark — GUIVM/BENCH Families (§15, §23–24)

| ID | Proposed Name (xUnit) | Level | Acceptance Criteria |
|---|---|---|---|
| GUIVM-01 | `SummaryViewModel_AnswersFiveQuestions_FromReportData` | unit | The 5 questions from §15 answered from the domain model/JSON (fake DEMO data from T06 while the real engine does not exist). GUI with no second engine: VM consumes the schema. |
| GUIVM-02 | `DestructiveActionLabels_AlwaysQuarantine_NeverDelete` | unit (string guard) | No GUI action string labels destruction as "delete"; canonical label "Move to quarantine". Textual guard on GUI resources/strings. |
| BENCH-01 | `DatasetGenerator_SameSeed_TwoRunsIdenticalListing` | bench-smoke | `scripts/bench/generate_dataset.py` (T04) run 2× with same seed: `find \| sort \| md5sum` identical. Confidence prerequisite for every benchmark. |
| BENCH-02 | `SmokeDataset500_CountersMatchParameters` | bench-smoke | Smoke dataset of 500: unique/duplicate/conflict/placeholder counts match passed parameters. |
| BENCH-03 | `FullScale1M_MetricsArtifactRecorded` | bench (outside PR) | §23 goal (1M files): runs in scheduled/manual window per T04 procedure (`docs/benchmark-harness.md`); publishes `metrics.json` (wall_clock, files_enumerated/read, bytes_read partial/full, peak RSS via `/usr/bin/time -v`, CPU). Regression criterion: increase of `files_full_hashed`/`bytes_read_full` or wall_clock > T04-defined limit requires investigation before merge. No number here is invented: what T04 measures is what counts. |

### 3.9 Security — SEG Namespace (cross-ref T02)

Reserved for `docs/threat-model.md` (card T02). Integration rule: **every
concrete threat model case points to exactly one `SEG-nn` with test name,
level, and criteria**; automatable cases go into `tests/Doctor.Tests`
(category `Security`), manual cases go into the final audit checklist
(section 4). If a T02 case matches an already-mapped test here (e.g.:
PLH-04, CACHE-04, NDES-03/04), T02 references the existing ID instead of
duplicating. Name collision resolves in T02; table IDs do not change.

---

## 4. §58 — Final Audit: Questions Becoming Tests

Each "CAN I TRUST THE DELETE BUTTON?" question with its executable
coverage. Status: AUTOMATED (test already mapped above) / PARTIAL (automated +
manual procedure in the audit).

| # | Question (§58) | Coverage | Status |
|---|---|---|---|
| Q1 | Can it delete the wrong file? | NDES-01..05; DET-05 (correct winner decision); manual review of static guard allowlist | PARTIAL |
| Q2 | Can it read a placeholder? | PLH-01..04 (dual evidence: hash + stream opening) | AUTOMATED |
| Q3 | Different results between scans? | DET-01, DET-02, DET-06 | AUTOMATED |
| Q4 | Depend on thread order? | DET-03 (parallel pool + shuffled order) | AUTOMATED |
| Q5 | Lose file during quarantine? | NDES-01 (manifest + hash), NDES-04 (mid-failure), QRT-06 (complete manifest); manual procedure: reconcile manifest × quarantine × source in large dataset | PARTIAL |
| Q6 | Restore to wrong place? | NDES-02 (exact path + hash), NDES-03 (never overwrites) | AUTOMATED |
| Q7 | Corrupt a file? | NDES-01/02 (pre/post-move and restore hash), HSH-01 (read limits) | AUTOMATED |
| Q8 | Inconsistent report? | RPT-01..03; CLI-02 | AUTOMATED |
| Q9 | Hide a real conflict? | HSH-04; GRP-02 (normalization does not swallow divergence) | AUTOMATED |
| Q10 | Classify different as identical? | HSH-02 (L2/L3 pipeline), HSH-03 against deliberately different pairs; property: known-different contents never classify as identical | AUTOMATED |
| Q11 | Lose cache incorrectly? | CACHE-01..04 | AUTOMATED |
| Q12 | Different behavior across machines? | DET-06 (golden file on both OS); BENCH-01 (reproducible generator) | PARTIAL |

The final audit (EPIC 19) executes this table as a checklist: AUTOMATED item =
green suite in release candidate; PARTIAL item = green test + manual evidence
logged in the audit card. Any YES to a §58 question = product not ready (§58).

---

## 5. Fixtures — Standard Synthetic Trees

Two complementary sources, no overlap:

1. **In-process builder** (`tests/Doctor.Tests/TestSupport/SyntheticTreeBuilder.cs`)
   — small/medium trees created in `Path.GetTempPath()` by the test itself.
   Deterministic: fixed literal contents, explicit mtimes
   (fixed `DateTimeOffset` per index, UTC), fixed seeds. No subprocess
   dependency — fast enough to run across the full suite.
2. **T04 Generator** (`scripts/bench/generate_dataset.py`, pure stdlib, flags
   `--files --dup-groups --conflict-names --placeholders --depth --seed`) —
   large datasets and the benchmark dataset; used by BENCH-* tests and the
   nightly suite. Contract with T04: same seed ⇒ same tree (proven by
   `find | sort | md5sum` twice); placeholders simulated via
   `.placeholder-meta.json` sidecar with documented hook for real Windows
   attributes.

Standard layout and naming (used by both):

```text
<root>/
  unique/                  unrelated files
  dups/                    exact identical copy groups
  conflicts/               same normalized base-name, different contents
  conflict-names/          real suffixes: "report (1).txt",
                           "photo (conflicted copy).jpg", "-DESKTOP-ABC123",
                           "~spreadsheet.xlsx", "~$doc.docx", ".sb-1a2b3c.pdf"
  deep/l0/l1/.../lN/       parameterized depth
  placeholders/            normal + OFFLINE/ROO/RODA/reparse (per OS)
                           + .placeholder-meta.json sidecar (T04 convention)
```

| Fixture ID | Source | Minimum Content | Used By |
|---|---|---|---|
| FX-MIN | builder | ~12 files: 1 identical pair, 1 conflict pair, 1 unique, 1 placeholder, 1 unicode trap name | DET-01/02/03, RPT-*, GRP-*, HSH-*, CLI-* |
| FX-UNICODE | builder | names that differentiate byte × locale order (uppercase/lowercase, accented, Turkish-I) | DET-04 |
| FX-TIE | builder | pairs with constructed identical mtime/size | DET-05 |
| FX-PLACEHOLDER | builder + sidecar | mix of normal/OFFLINE/ROO/RODA/reparse | PLH-01/02 |
| FX-LOOP | builder | cyclic junction (Win) / symlink (Unix) | PLH-04 |
| FX-GOLDEN | builder, committed values | fixed tree + committed golden report in `tests/Doctor.Tests/Fixtures/golden/` | DET-06, Q12 |
| FX-SMOKE500 / FX-1M | T04 generator | 500 files / 1M files, fixed documented seed | BENCH-01..03, nightly load |

Rules: no fixture carries sensitive data; mtimes always explicit (never
`now()`); ASCII names in generic fixtures, unicode isolated in FX-UNICODE
to locate sort failures; test quarantine ALWAYS outside the scanned root,
except in T02 dedicated vectors.

---

## 6. CI — GitHub Actions Plan

Single file `.github/workflows/ci.yml`. Triggers: `push` on `main`,
`pull_request`, `schedule` (nightly cron for BENCH-01/02). .NET 8 SDK from
official setup-action; NuGet cache by lockfile hash.

```yaml
name: ci
on:
  push: { branches: [main] }
  pull_request:
  schedule: [{ cron: "0 5 * * *" }]   # nightly: bench smoke

jobs:
  linux:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 8.0.x }
      - run: dotnet build CloudSyncConflictDoctor.sln -c Release
      - run: dotnet test tests/Doctor.Tests -c Release --no-build
             --filter "OS!=Windows"
             --collect:"XPlat Code Coverage"
             --logger "trx;LogFileName=linux.trx"
      - run: dotnet test tests/Doctor.Tests -c Release --no-build
             --filter "Category=Determinism"     # EXPLICIT determinism on Linux too
      - uses: actions/upload-artifact@v4
        with:
          name: linux-results
          path: |
            tests/**/TestResults/**
            **/coverage.cobertura.xml
            determinism-hashes.txt

  windows:
    runs-on: windows-latest      # job where native tests run
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 8.0.x }
      - run: dotnet build CloudSyncConflictDoctor.sln -c Release
      - run: dotnet test tests/Doctor.Tests -c Release --no-build
             --collect:"XPlat Code Coverage"
             --logger "trx;LogFileName=windows.trx"   # FULL SUITE
      - uses: actions/upload-artifact@v4
        with: { name: windows-results, path: tests/**/TestResults/** }
```

Plan decisions:

1. **Two jobs, no matrix:** `ubuntu-latest` and `windows-latest`. The Windows
   job is where `native-windows` tests live (real OFFLINE/ROO/RODA attributes,
   junctions, FindFirstFileEx, seek penalty); Linux runs everything else,
   including determinism — §20 requires the determinism test in CI, and running it
   on both OS is what gives the golden file (DET-06) its power against
   platform differences.
2. **Determinism is a separate check** (`Category=Determinism`) on Linux for
   appearing as its own line in PR summary; on Windows it is part of the full
   suite.
3. **Artifacts:** TRX from each job, coverage (coverlet XML + report), and
   `determinism-hashes.txt` (SHA-256 of scan A/B/C reports) published
   per run — auditable evidence of determinism per build. 30-day retention.
4. **Mandatory branch protection checks:** `linux / test` and
   `windows / test`. PR without both greens does not merge.
5. **Benchmark:** only BENCH-01/02 on nightly cron (cheap). BENCH-03 (1M)
   is manual/scheduled long-run, outside PR, with `metrics.json` artifact
   per `docs/benchmark-harness.md` from T04.
6. Coverage published as artifact and commented on PR (per-module summary);
   section 8 thresholds fail the build, not advisory.

---

## 7. Minimum Coverage per Module

Measurement: coverlet.collector, line count, gate by threshold in CI.
Exclusions: `Program.cs`, GUI code-behind/views, generated code.

| Module | Minimum Line | Notes |
|---|---|---|
| `src/Doctor.Core` | **85 %** | Critical classes with 100% line target: `PathByteComparer`, tie-break resolver, placeholder gate, quarantine mover, report writer |
| `src/Doctor.Cli` | 75 % | Exit code flows covered by CLI-01..04 |
| `src/Doctor.Gui` (ViewModels) | 60 % | Only ViewModels count; views excluded from measurement |
| `src/Doctor.Gui` (views) | — | Excluded; UX validated by GUIVM-* and inspection |
| `tests/Doctor.Tests` | — | It is the instrument, not the target |

Coverage is a floor, not a target: a module with 100% line coverage and
red DET/PLH/NDES is not ready. The inversion never applies.

---

## 8. Gates — Which Suites Must Be Green (§45)

| Gate | Required Green Suites (category) | OS |
|---|---|---|
| **GATE 1 — Architecture Ready** | This document approved is a gate item; no suite yet (skeleton non-existent) — condition: T01 contracts cover the section 2 seams | — |
| **GATE 2 — Scanner Correctness** | `Determinism` (DET-01..06), `Placeholder` (PLH-01..04), `Grouping`, `Hashing` (HSH-01..04), `Cache` (CACHE-01..04), `Report` — plus `Doctor.Core` coverage ≥ 85 % | **both** |
| **GATE 3 — Resolution Safety** | `Safety` (NDES-01..05, QRT-06) + automated `SEG-*` cases from T02 linked to quarantine/restore/TOCTOU | both (native on Windows) |
| **GATE 4 — Performance** | BENCH-01/02 green in CI; BENCH-03 executed with `metrics.json` archived; no regression per T04 criteria | Linux (1M may be on dedicated host) |
| **GATE 5 — Security** | All automated `SEG-*` from threat model + PLH-04 + CACHE-04 + installer review (when it exists) | both |
| **GATE 6 — Release** | ALL categories green on both jobs; mandatory CI checks passing; determinism golden (DET-06) green on both OS; CLI-01..04 green | both |

Locking rule: previous gate closed is prerequisite for the next;
red suite in an open gate category freezes the corresponding downstream
(no GUI released on red GATE 2, no installer signed on red GATE 3).

---

## 9. Conditions on Other Cards and Risks

| Condition | For | Effect if Not Met |
|---|---|---|
| Section 2 seams (`IFileEnumerator` without guaranteed order, spyable `IHasher`, injectable `TimeProvider`, relative paths in report, injectable quarantine root) | T01 (`docs/contracts.md`), T03 (schema) | Byte-by-byte tests (§20) and non-opening proof (§21) become impossible; contracts would have to change after code exists |
| Exact serialization format (UTF-8 without BOM, fixed newline, declared key order) | T03 | DET-01/02/06 meaningless |
| Deterministic generator with documented flags + placeholder sidecar | T04 | BENCH-* and large FX datasets not trustworthy |
| Unique `SEG-nn` names pointing to this table's IDs | T02 | Coverage duplication and silent gaps in final audit |
| Avalonia confirmed/fixed (ADR-0009) and T06 VM skeleton | T01, T06 | GUIVM-01/02 adjust names, not criteria |

Recorded risks:

1. **Real placeholders exist only on Windows.** Mitigated by dual PLH layer
   (fake on any OS + native on Windows job); T04 sidecar covers Linux, and
   the limitation is documented on both cards.
2. **Golden file fragile to legitimate schema changes.** Rule: bump of
   `report_schema_version` regenerates golden IN THE SAME PR that changes
   schema, with justification in PR body — golden never updated "just to
   go green again".
3. **Frozen time may hide timestamp formatting bugs.**
   Accepted: timestamp format is validated by RPT-01 against schema; real
   value is the production `TimeProvider`'s responsibility, exercised in
   integration without freezing (DET-03 uses real pool).
4. **GitHub Actions Windows job is slower and limited** — accepted; it is where
   the SPEC mandates testing native behavior, and the cost is paid only in CI,
   not in the local loop.
