# Round 01 — 1M File Benchmark (GATE 4)

| Field | Value |
|---|---|
| Card | t_f3cb006a (T07 — 1M benchmark run + GATE 4 logging) |
| Date | 2026-08-23 |
| Revision | 1 |
| Owner | forg3 |
| Branch / worktree | wt/t_f3cb006a @ .worktrees/t_f3cb006a |
| Scanner code commit | 0ca0530 (main — no code changes in this run) |
| Status | Executed — initial benchmark baseline (GATE 4) |

## 1. Objective and Scope

First benchmark run on the 1,000,000 file dataset
(docs/benchmark-harness.md §5), establishing the measurement baseline that GATE 4
(SPEC §45) requires: benchmark executed, metrics logged, regressions
documented. Being the first run, there is no BEFORE: values here are the
**baseline** against which future runs will be compared (harness §6, rule 4).

**Scope of executed stage (explicit declaration required by card decision 3):**
the available scanner executable is Doctor.Cli on main (commit 0ca0530),
which runs the L0 pipeline (sidecar-aware enumeration) → L1 (grouping) → L2
(BLAKE3 partial hash). The full-hash verdict stage (L3 →
identical_duplicates / real_conflicts) does not produce results in this build:
`files_full_hashed = 0`, `bytes_read_full = 0`, `identical_duplicates = [],
real_conflicts = []`. This run's numbers are REAL and measured — nothing was
simulated — but cover only up to L2. Future runs with L3 integrated must use
this table as BEFORE.

## 2. Dataset

Generator `scripts/bench/generate_dataset.py`, `generator_version = 1`
(deterministic by seed), composed in 10 shards per harness §5:

```text
root:       /tmp/cd-1m (union of shard_0 .. shard_9 subtrees)
per shard:  --files 100000 --dup-groups 5000 --conflict-names 5000
            --placeholders 6000 --depth 6
seeds:      20260822, 20260823, ..., 20260831  (shard_0 .. shard_9)
generation: 14:33:16 – 14:47:06 UTC (13m50s), sequential, log in generation.log
```

Composition verification (`count_verification.json`, verify_counts.py script):

```text
files found:     1,060,000  (1,000,000 target + 60,000 .placeholder-meta.json sidecars)
files expected:  1,060,000  (sum of files_created + sidecars_written from 10 summaries)
divergence:      0          (all 10 shards match exactly)
bytes on disk:   76,015,949,061 B (~70.8 GiB)
bytes forecast:  75,999,732,750 B (generator summary; difference = directory overhead)
```

Per-shard generation JSON summaries versioned in `gen_shard_0..9.json`.

## 3. Machine Conditions

Logged in `conditions.txt` (run start):

```text
host:      vnic, Ubuntu 24.04 (kernel 6.17.0-1018-oracle), aarch64, 4 vCPU VM
memory:    23 GiB total / ~14 GiB available, no swap
disk:      ext4, 153 GiB free before generation (193 GiB total)
inodes:    25,176,611 free before generation
load:      load average 1.47–3.08 during run — host shared with
           parallel Hermes work sessions (scans and builds of sibling cards).
           Absolute numbers inherit this noise; future before/after comparisons
           should repeat equivalent conditions.
/usr/bin/time absent → peak_RSS and CPU% via stdlib wrapper
resource.getrusage(RUSAGE_CHILDREN) (harness §3), implemented in run_scan.py.
```

## 4. Procedure

1. Doctor.Cli Release Build in run worktree (0 new warnings).
2. Smoke validation on 500-file dataset (seed 42): telemetry emitted,
   zero gate, exit 0.
3. 1M dataset generation and verification (section 2).
4. Runs on `/tmp/cd-1m`: 1 disposable warm-up + 5 measured
   (`run_scan.py` → `runwarmup`, `run1..run5` + `.meta.json`), all exit 0.
5. Median aggregation of 5 (`aggregate_round01.py`) and determinism
   verification (`compare_determinism.py`, `fingerprint_runs.py`).

Command per run: `dotnet conflictdoctor.dll scan /tmp/cd-1m --json`.

## 5. Model Table (harness §6)

Median of 5 runs. Baseline: no BEFORE (first run).

```text
dataset:        generator_version=1 seeds=20260822..20260831
                params=10x(--files 100000 --dup-groups 5000 --conflict-names 5000
                           --placeholders 6000 --depth 6)
before_commit:  n/a — initial baseline (code at 0ca0530)
after_commit:   n/a — first run; values below are the BASELINE

wall_clock_s:            92.171   (runs: 118.64 / 92.17 / 121.17 / 76.62 / 20.21)
files_enumerated:        1,000,000
files_read:              16,572   (= files_partial_hashed; see L2 scope in section 1)
bytes_read:              1,364,973,038
bytes_read_partial:      1,364,973,038
bytes_read_full:         0        (L3 outside executed stage)
full_hash_count:         0        (same)
peak_RSS_kb:             1,040,328  (~1.02 GiB)
CPU%:                    34.6     (user+sys/wall via getrusage)
placeholder_bytes_read:  0 -> 0   GATE: PASS in all 5 runs
```

Complementary counters (median, identical across 5):

```text
files_placeholder:       60,000
files_skipped:           0   (current emission only counts access errors; see section 7)
candidate groups (L1):   8,286  (all with 2 members: 2 x 8,286 = 16,572 L2 opens)
listed placeholders:     60,000
access errors:           0
exit code:               0 (clean) in all 6 runs
```

## 6. Determinism Evidence

- All 6 stdouts have identical size (12,413,941 B); differ only in the
  `generated_from` block (live timestamps, allowed by ADR-0003).
- Block removed, SHA-256 of canonical content is EQUAL across all 6 runs:
  `1019fd09a88ebef958a87eb10ca3749ff4ef4a956e1fba17390d73d52d103a96`
  (`determinism_check.json`).

## 7. Observations, Deviations, and Limitations

1. **L0–L2 Scope**: no L3 verdicts in this build, `bytes_read` covers only
   partial/full windows ≤ 128 KiB (ADR-0005 recipe). The SPEC §24 key metric
   ("fewer full hashes") has no counterproof in this run;
   next run (with L3 integrated) uses this table as BEFORE.
2. **Bytes avoided (derived from report, not emitted)**: of
   75,999,732,750 logical bytes, 1,364,973,038 bytes were read → **98.2% of bytes
   did not need to be read** (98.34% of enumerated files never opened:
   923,428 unique + 60,000 placeholders). Value dependent on L2 scope.
3. **Telemetry contract deviation**: the invariant documented in
   Telemetry.cs (`files_enumerated = placeholder + skipped + partial_hashed`)
   does not close with current emission — `files_skipped = 0` because CLI only
   counts access errors in that counter; the 923,428 unique files discarded by
   grouping enter no counter. Pending card for contract
   alignment (T06/T16); does not affect gate or other metrics.
4. **High wall_clock variance** (20–121 s): progressive page cache warmup between
   runs + sibling session load on host. Median (92.171 s)
   follows harness §4 protocol. For tighter future comparisons,
   consider optional cold-cache (§4, requires root).
5. **Inherited harness limitations**: simulated placeholders on ext4 (stub +
   sidecar, §2.1 — definitive validation only on NTFS/Windows); 4 vCPU ARM
   virtualized host under shared load — absolute numbers only valid for
   relative before/after comparison on same host, not as product guarantee.

## 8. Run Files (results/bench/round-01/, versioned on branch)

| File | Content |
|---|---|
| `runwarmup`, `run1..run5` | complete scanner JSON stdout (12.4 MB each) |
| `run*.meta.json` | wall/RSS/CPU/exit per run (getrusage wrapper) |
| `gen_shard_0..9.json` | per-shard generation JSON summary |
| `generation.log`, `scans.log` | generation and run chronology |
| `count_verification.json` | 1,060,000 == 1,060,000 verification |
| `aggregate_output.txt` | raw aggregation output (median of 5) |
| `fingerprints.json`, `determinism_check.json` | determinism evidence |
| `conditions.txt` | machine state at start |
| `run_scan.py`, `generate_1m.sh`, `run_all_scans.sh`, `verify_counts.py`, `aggregate_round01.py`, `inspect_telemetry.py`, `compare_determinism.py`, `fingerprint_runs.py`, `summary_counts.py` | reproducible methodology |

Dataset `/tmp/cd-1m` removed after this run's logging (harness §5 — 76 GB
do not remain on disk); it is byte-for-byte regenerable via section 2 commands.
