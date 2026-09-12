# Cloud Sync Conflict Doctor

> "You have 1,847 duplicate files and 23 real divergences in this folder. 1,812 are byte-identical copies — I can quarantine them now."

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT) [![FOSS](https://img.shields.io/badge/FOSS-Free%20%26%20Open%20Source-green.svg)](LICENSE) [![Status](https://img.shields.io/badge/status-in%20development-orange)](README.md) [![Stack](https://img.shields.io/badge/C%23-.NET%208-blueviolet)](CloudSyncConflictDoctor.sln) [![Safety](https://img.shields.io/badge/safety-quarantine%20only-red)](README.md) [![Tests](https://img.shields.io/badge/tests-541%20passed-brightgreen)](tests/Doctor.Tests)

**Author:** forg3

## What it does

Anyone who uses OneDrive, Google Drive, Dropbox, Nextcloud, or iCloud knows the routine: `document (conflicted copy).docx`, `spreadsheet-DESKTOP-A1B2C3.xlsx`, `photo (1).jpg`, `photo (2).jpg`... The cloud provider creates these copies silently, never tells you which version is correct, and the folder rots for years because the user is afraid of deleting the wrong one.

**Cloud Sync Conflict Doctor** scans a locally synced folder and answers, with auditable evidence:

- how many copies are **byte-identical** (can be quarantined risk-free);
- which are **real divergences** of the same document (need human decision);
- which files are *online-only placeholders* (it won't touch them — see below);
- how much space can be safely recovered.

It is a trustworthy decision tool for cleaning up sync trees — not another dumb deduplicator that doesn't understand conflict.

## How it works

Deterministic cascade pipeline — performance comes from **not reading** what doesn't need to be read:

```text
LEVEL 0  Enumeration (metadata only: path, size, mtime, attributes, file id)
         → OFFLINE / RECALL_* / reparse point placeholders are MARKED AND SKIPPED.
           Never opened: touching them would trigger a full cloud download.
LEVEL 1  Grouping by (normalized base name + size)
         → single-element group? Discarded without reading 1 byte.
         → limited, versioned normalization of conflict suffixes
           ((conflicted copy), -DESKTOP-XXXX, " (1)", ~$, .sb-hex…)
LEVEL 2  Partial BLAKE3 hash (first 64 KiB + last 64 KiB) on survivors only
         → kills false positive "same name, same size, different content".
LEVEL 3  Full BLAKE3 hash only on Level 2 collisions
         → same hash = identical duplicate | different hash = real divergence.
```

Design guarantees (all tested, not declared):

- **Byte-for-byte determinism:** the same tree produces the same JSON report every time — path ordering by UTF-8 bytes (never locale), tie broken by `mtime → size → path`, zero parallelism in decision-making (parallelism only in I/O).
- **Safety before convenience:** nothing is deleted. Ever. Every removal becomes a **dated quarantine** with a manifest (`original_path`, BLAKE3 hash, reason, rule) and **verified restore**, which never silently overwrites an existing file.
- **Versioned BLAKE3:** algorithm explicit in the report schema (`algorithm/hash_version/report_schema_version`); changing the hash is a format bump.
- **Incremental SQLite cache** keyed by file ID/inode — renaming/moving doesn't invalidate the cache; the second scan is nearly instant.
- **Absolute local-first:** zero upload, zero mandatory telemetry, zero content sent to any service.
- **Media calibration:** SSD/NVMe → high read parallelism; HDD with seek penalty → serial read (detected via IOCTL, configurable, never hardcoded).

Distribution: CLI (`conflictdoctor scan <folder> --json`) from day one, consumer GUI as the main product (flow: Folder → Scan → Summary → Duplicates → Conflicts → Compare → Quarantine → Confirmation).

## Stack

C#/.NET 8 · solution `CloudSyncConflictDoctor.sln`: `src/Doctor.Core` (engine), `src/Doctor.Cli`, `src/Doctor.Gui` (Avalonia — working hypothesis), `tests/Doctor.Tests`. SQLite via `Microsoft.Data.Sqlite`, hashing via Blake3.

## Current state (August 2026)

Completed and reviewed (**GATE 1 — Architecture Ready closed**):

- ADRs 0001–0011 (language, quarantine, schema, scanner, hashing, cache, concurrency, CLI, GUI, detailed quarantine, comparator);
- Threat model with 11 concrete data destruction cases and mitigations mapped;
- Report schema v1 closed with real fixture (BLAKE3 hashes calculated);
- Complete test strategy (DET/PLH/QDT suites mapped to gates);
- Benchmark harness + versioned synthetic dataset generator;
- Functional Level 0 prototype (cross-platform enumeration + placeholder marking at source);
- Navigable GUI skeleton with the 9 screens of the flow and safety visual language (destructive action always labeled "Move to quarantine", never "delete"), ViewModels with 49 green tests.

In progress: scan engine (Level 0/1 pipeline), zero-bytes placeholder gate with telemetry, GUI state machine, telemetry contract (bytes avoided).

## What's missing (roadmap to release)

| Milestone | Content | Gate |
|---|---|---|
| Complete engine | Level 1–3, duplicate vs divergence classification | GATE 2 (Scanner Correctness) |
| Security | Path traversal, TOCTOU, reparse attacks, fail-closed | GATE 5 |
| Resolution | keep-newest/largest/machine/manual + deterministic tiebreak | GATE 3 (Resolution Safety) |
| Performance | 1M file benchmark, bytes-avoided telemetry | GATE 4 |
| CI | GitHub Actions ubuntu+windows, determinism/placeholder/no-delete suites | GATE 6 |
| v1 distribution (free) | Self-contained win-x64 build via GitHub Releases (no code signing — deferred to paid v2) | GATE 6 |
| Final audit | *"Can I trust the delete button?"* — adversarial review | pre-RC |

> **Commercial strategy:** v1 is **free** for field validation. Installer packaging with signed code (MSIX/winget/Store), Ed25519 licensing, and pricing are reserved for **v2**, when the product is proven.

## Current state (2026-08-23, evening) — RC1

- **Kanban board completed: 70/70 cards done** — all EPICs, QA suites, S11 complete,
  GATEs 1/2 closed with evidence, GATE 4 baseline, GATE 5 consolidated, GATE 6 structural
  dependency documented (Actions disabled + Windows job)
- **EPIC 19 executed:** *"Can I trust the delete button?"* → verdict **YES**
  (`docs/audit/epic19-can-i-trust-the-delete-button-2026-08-23.md`)
- **RC1 generated:** `conflictdoctor.exe` win-x64 self-contained (67 MB), E2E smoke test passed
  (`docs/release/RC1-notes.md`)
- GATE 2/6 audit with real evidence: `docs/qa/auditoria-gate26-2026-08-23.md`

## History — afternoon of 2026-08-23

- **522 green tests** on `main`; 83.4% line-rate coverage (coverlet)
- **Local CI**: `./scripts/ci/local-ci.sh` — Release build + full suite + static guards
  (anti-`File.Delete`/`Directory.Delete`, anti-`Process.Start`, GUIVM-02 anti-destructive-label)
- **S11 Hardening completed:** PathCanonical (S11-1), ReparsePolicy (S11-2 via T18),
  TOCTOU+cache poisoning (S11-3/4 via T19), fail-closed with auditable `UnresolvedGroup` (S11-5),
  GATE 5 audit consolidated with SEG-01..23 matrix (S11-6) and SEG-12 green
  (structural exclusion of `ConflictDoctor/` in enumeration)
- **v1 Comparators complete:** text (LCS), binary, markdown and CSV (ADR-0011)
- Benchmark round-01 on real 1M file dataset recorded in `docs/bench/rounds/`
- Closed gates: 1 (Architecture), 2 (Scanner Correctness); GATE 4 baseline recorded

## Open questions

- **Semantic Office diff** (paragraphs/cells/formulas via Open XML): goes into v1 or is the Pro hook? Analysis in progress on the board.
- **Provider scope in v1**: all five at once or progressive Windows-first? (architecture already vendor-decoupled.)
- **Naming**: "Conflict Doctor" describes but doesn't sell — name/trademark research planned.
- Line/column-oriented CSV comparison and markdown diff in the v1 comparator.
- MSP/RMM mode: headless scheduled execution with consolidated JSON report (the EXE is the product; PowerShell wrapper is just deployment).

## CI/CD — local (Linux) vs GitHub Actions

With **GitHub Actions for the org disabled this month**, CI runs **locally** via
`./scripts/ci/local-ci.sh`:

| Step | Actions equivalent | Status |
|---|---|---|
| Release Build (`dotnet build -c Release`) | linux job | ✓ green |
| Full suite (`dotnet test`) | linux job | ✓ 541/541 |
| Anti-`File.Delete`/`Directory.Delete` guard in `src/` | — (extra) | ✓ PASS |
| Anti-`Process.Start` guard in `src/` | — (extra) | ✓ PASS |
| GUIVM-02 guard (zero destructive labels in GUI) | — (extra) | ✓ PASS |
| Doctor.Core coverage (coverlet) | — (extra) | ✓ 93.37% |

**Known limitation:** local CI covers the **Linux version only**. The Windows job in the
workflow (`.github/workflows/ci.yml`) requires a native Windows OS — the real OneDrive
placeholder tests (PLH-03), NTFS junctions, and FileId-per-volume tests use Cloud Filter
APIs that don't exist on Linux (Wine doesn't implement the Cloud Filter API, so it's not
a valid alternative). Cross-compilation to Windows is already proven: the RC1
(`conflictdoctor.exe` win-x64 self-contained) was built right here. When Actions are
re-enabled, the push triggers the workflow and the Windows job runs the native tests —
the final requirement to formally close GATE 6.

## Development

This project runs via **Hermes Kanban** (board `conflict-doctor`): cards with Definition of Done, explicit dependencies, formal review gates, and auditable handoffs. Full specification at [`docs/SPEC.md`](docs/SPEC.md) and architecture decisions at [`docs/adr/`](docs/adr/).

```bash
# build and tests (requires .NET 8 SDK)
dotnet build CloudSyncConflictDoctor.sln
dotnet test tests/Doctor.Tests
```

---
 
**Golden rule of the product:** the user needs to get to the point of saying *"I know exactly why these files were chosen and I know I can undo it."*

## License

This project is Free and Open Source Software (FOSS) licensed under the [MIT License](LICENSE).
Copyright (c) 2026 forg3.
