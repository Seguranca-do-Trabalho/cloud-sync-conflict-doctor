# Benchmark Harness — Cloud Sync Conflict Doctor

| Field | Value |
|---|---|
| Card | t_2c4c7be7 (T04 — Benchmark harness design) |
| Date | 2026-08-22 |
| Revision | 1 |
| Owner | forg3 |
| Status | Active — reference for GATE 4 (Performance) |

## 1. Objective

Measure the scanner against SPEC §10, §23, and §24 requirements. The primary metric
is **not** `scan_seconds`: it is a function of time + bytes read + files
actually opened + memory. The benchmark exists to prove three things after
each relevant change:

1. fewer files read;
2. fewer bytes read;
3. fewer full hashes —

without regression in time, memory, and without ever reading a placeholder
(`placeholder_bytes_read == 0`, SPEC §6; violation is a security failure, not
a performance metric).

House rule: **never optimize by feeling; always benchmark before and after**
(SPEC §23).

## 2. Versioned Dataset

Tool: `scripts/bench/generate_dataset.py` (pure python3, stdlib,
`generator_version: 1`). Deterministic by seed: same seed produces byte-identical tree
(content, paths, sizes, and mtimes fixed at
2000-01-01T00:00:00Z), verifiable by:

```bash
for R in $A $B; do (cd "$R" && find . -type f -printf '%P\n' | LC_ALL=C sort | md5sum; \
  find . -type f -print0 | LC_ALL=C sort -z | xargs -0 md5sum | md5sum); done
```

Parameters:

```bash
python3 scripts/bench/generate_dataset.py \
    --root /tmp/cd-dataset --files 500 --dup-groups 25 \
    --conflict-names 40 --placeholders 30 --depth 4 --seed 42
```

Resulting distribution (exact partition of `--files`):

| Category | Formula | Example (500/25/40/30) |
|---|---|---|
| Identical duplicate groups | `--dup-groups`, 2 files each, same BLAKE-identical content | 25 groups / 50 files |
| Real divergences | `--conflict-names`, 2 files each, normalized name equal, content differs; even-numbered groups have the **same size** (worst case for pipeline size filter), odd have different sizes | 40 groups / 80 files |
| Simulated placeholders | `--placeholders`, 4 KiB stub + `.placeholder-meta.json` sidecar | 30 (+30 sidecars) |
| Unique | remainder | 340 |

Generated conflict suffixes follow the fixed SPEC §7 list: `" (1)"`,
`" (conflicted copy)"`, `-DESKTOP-XXXX`, `~$` (Office-lock prefix), `~`,
`.sb-<hex>`.

### 2.1 Placeholders on Linux/ext4 — Limitation and Windows Hook

`FILE_ATTRIBUTE_OFFLINE`, `RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS`, and reparse
points **do not exist on ext4**. The generator simulates behavior:

- 4 KiB stub whose content begins with the readable marker
  `PLACEHOLDER-SIMULATED` — if the scanner opens by mistake, it reads bytes > 0 and the
  violation becomes detectable in the benchmark;
- `<file>.placeholder-meta.json` sidecar recording `simulated_attributes`
  (simulated OFFLINE-family attribute), `provider`, simulated `original_size`
  and limitation note.

The sidecar is the source of truth for the harness to decide which paths were
placeholder in the scan. **Windows Hook:** when the scanner runs on NTFS, the
benchmark should validate real attributes via Level 0 enumeration
(`FILE_ATTRIBUTE_*` + reparse) instead of the sidecar; the expected path list
still comes from the same dataset. Corresponding automated test: scan of the
dataset must end with `placeholder_bytes_read == 0`; a scanner treating the
stubs as normal files would read `30 × 4 KiB` more in `bytes_read`, which
constitutes a failure, not an acceptable variation.

### 2.2 Reproducibility

- All randomness comes from `random.Random(seed)`; no use of time,
  randomized hash, or filesystem order in path/content decisions.
- `os.utime` fixes identical mtime/atime on all files.
- The JSON summary emitted on stdout contains `generator_version`, parameters, and
  counts — paste this summary into the run log (§6).

## 3. Mandatory Metrics

Source A: scanner JSON report `telemetry` block (ADR-0003 schema).
Source B: process via GNU time. Source C: runner monotonic clock.

| Metric | Source | Definition |
|---|---|---|
| `wall_clock_time` | C | total scan time, monotonic |
| `files_enumerated` | A | entries enumerated at Level 0 |
| `files_skipped` | A | discarded before open (grouping) |
| `files_placeholder` | A | treated as DO NOT TOUCH |
| `files_partial_hashed` / `partial_hash_count` | A | survived Level 2 (64 KiB start + 64 KiB end) |
| `files_full_hashed` / `full_hash_count` | A | full BLAKE3 (Level 3) |
| `bytes_read_partial` | A | bytes read during partial hash |
| `bytes_read_full` | A | bytes read during full hash |
| `bytes_read` | A | partial + full sum |
| `placeholder_bytes_read` | A | **must always be 0** (gate, not metric) |
| `peak_RSS` | B | `Maximum resident set size (kbytes)` from `/usr/bin/time -v` |
| `CPU` | B | `%CPU` from `/usr/bin/time -v` (user+sys/wall) |

Environment note: this development host does not have `/usr/bin/time`
installed (GNU time). Install with `sudo apt-get install time`; while
not possible, capture `peak_RSS` via stdlib wrapper:

```bash
python3 - <<'EOF'
import resource, subprocess, sys, time
t0 = time.perf_counter()
subprocess.run(sys.argv[1:], check=True)
dt = time.perf_counter() - t0
rss_kb = resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss
print(f"wall_clock_s={dt:.3f} peak_rss_kb={rss_kb}")
EOF
```

(`ru_maxrss` in KiB on Linux; equivalent to GNU time field.)

## 4. Run Procedure (Before/After Changes)

1. **Freeze dataset**: same generator version, same parameters, same
   seed. Record the generation JSON summary. Recommended: seed 42 for smoke,
   seed 20260822 for full run.
2. **Machine state**: machine plugged in, no other significant load
   (`uptime` load < 0.5), same temperature/range N/A on VM — note
   host, commit, and conditions in the log.
3. **Run N = 5 scans** of the binary/CLI in the version BEFORE the change:
   ```bash
   /usr/bin/time -v conflictdoctor scan /tmp/cd-dataset --json > run_before_$.json
   ```
   Discard the first run (page cache warmup) and report the
   **median** of the 5 following. For cold-cache measurement (optional, requires
   root): `sync && echo 3 | sudo tee /proc/sys/vm/drop_caches` before each
   run and log that the run is cold.
4. Apply the change (separate commit), repeat step 3 as AFTER.
5. Fill in the comparison table (§6) with medians and % deltas.
6. Archive `run_before_*.json` / `run_after_*.json` with the log.

The benchmark always compares two versions on the SAME dataset; never compares
versions across different datasets.

## 5. 1,000,000 File Goal (SPEC §23–24) and Partitioning

Target command (DO NOT run routinely; ~77 GB, ~15 min generation):

```bash
python3 scripts/bench/generate_dataset.py \
    --root /tmp/cd-1m --files 1000000 --dup-groups 50000 \
    --conflict-names 50000 --placeholders 60000 --depth 8 --seed 20260822
```

Estimated budget from smoke (~77 KiB/file average):
space ~77 GB, inodes ~1.06 M (host has 26 M inodes, 172 GB free — fits, but
reserve the disk). Estimated generation between 10 and 20 min (530-file smoke
took less than 1 s).

Generation and scan partitioning:

1. **Seeded-shard generation**: to avoid holding the entire plan in memory
   or a single giant directory, generate K independent shards and compose root:
   ```bash
   for i in $(seq 0 9); do
     python3 scripts/bench/generate_dataset.py \
       --root /tmp/cd-1m/shard_$i --files 100000 --dup-groups 5000 \
       --conflict-names 5000 --placeholders 6000 --depth 6 --seed $((20260822 + i))
   done
   ```
   Each shard is internally deterministic; composition is just subtree union.
   Scanner runs over `/tmp/cd-1m` (common root).
2. **Partitioned scan for diagnosis**: if isolating costs is needed, scan
   shard by shard and sum telemetries; sum should match root scan
   within root enumeration overhead (log the difference).
3. **Runner memory**: `peak_RSS` at 1M goal validates the requirement that scanner
   does not load entire inventory unnecessarily; if it blows up, Level 0
   design needs streaming, not higher benchmark tolerance.

GATE 4 (SPEC §45) closes only with this run executed and logged.

## 6. Regression Criteria

Per-run model table (median of 5, deltas vs. BEFORE):

```text
dataset:        generator_version=1 seed=... params=...
before_commit:  <sha>
after_commit:   <sha>
wall_clock_s:   ... -> ...   (Δ%)
files_enumerated: ... -> ... (Δ%)
files_read:     ... -> ...   (Δ%)
bytes_read:     ... -> ...   (Δ%)
bytes_read_partial / full: ... -> ...
full_hash_count: ... -> ...  (Δ%)
peak_RSS_kb:    ... -> ...   (Δ%)
CPU%:           ... -> ...   (Δ%)
placeholder_bytes_read: 0 -> 0   (gate)
```

Rules:

1. **Absolute gate**: `placeholder_bytes_read != 0` fails the run
   regardless of any performance gain.
2. **Regression**: any of `wall_clock_time`, `bytes_read`,
   `files_full_hashed`, `peak_RSS` worsening **more than 3%** in median fails the
   change, except ≥ equal magnitude improvement in another metric of the §24
   function, justified in the log.
3. **Valid improvement**: reducing `bytes_read` or `full_hash_count` without worsening
   time beyond item 2 is a priority win, in SPEC §51 order
   (correctness > safety > determinism > preservation > performance).
4. Report format changes require new `report_schema_version`
   (ADR-0003) and re-baseline: first run after bump counts only as BEFORE.

## 7. Known Limitations

- ext4 does not expose placeholder attributes; simulated via stub + sidecar
  (§2.1). Definitive placeholder validation occurs on NTFS/Windows with
  real attributes.
- Virtualized environment (4 vCPU): absolute numbers serve for relative
  before/after comparison on same host; do not publish absolute throughput as
  product guarantee.
- `/usr/bin/time` absent on this host until GNU time package is installed;
  use the §3 wrapper during that period.
