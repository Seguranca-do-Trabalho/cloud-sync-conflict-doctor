# Security Audit + Installer Review — T20 (S11-6)

| Field | Value |
|---|---|
| Task | t_ea884c03 — T20 - S11-6: Security audit + installer review |
| Board | conflict-doctor |
| Date | 2026-08-23 |
| Revision | R1 |
| Owner | Review worker (ox-alpha), Seguranca-do-Trabalho org |
| Scope | Static analysis of src/ (Doctor.Core, Doctor.Cli, Doctor.Gui) + installer state check |
| Method | API-directed grep of I/O + full reading of affected files + suite execution |
| Role | REVIEWER — no code implemented or committed in this task |

## 0. Baseline Verified in This Execution

- Build + tests: `dotnet test CloudSyncConflictDoctor.sln` → **Passed! Failed: 0, Passed: 541** (executed by reviewer, actual output).
- Audited base: main @ 399e353 (T16 merge).

## 1. Checklist 1 — Destructive APIs with User Arguments (Prohibited)

Patterns scanned in src/ (excl. bin/obj): `File.(Delete|Move|Replace|Copy|Open)`, `Directory.(Delete|CreateDirectory|Move)`, `DeleteDirectory`, `FileSystem.`.

Aggregate result: **zero occurrences of `File.Delete`, `Directory.Delete`, or `DeleteDirectory` in all of src/**. Mutant API occurrences found, classified one by one:

| Location | Call | Arguments | Classification |
|---|---|---|---|
| Doctor.Core/Quarantine.cs:152 | `Directory.CreateDirectory(quarantineRoot)` | injected `plan.RootPath` + fixed `ConflictDoctor/quarantine` segments | Safe — internal product path (SPEC §18), never raw argv |
| Doctor.Core/Quarantine.cs:160 | `Directory.CreateDirectory(payloadDir)` | staging under quarantineRoot | Safe — internal |
| Doctor.Core/Quarantine.cs:383 | `Directory.CreateDirectory(GetDirectoryName(destino))` | parent of `original_path` from manifest | See Note A |
| Doctor.Core/CacheStore.cs:44 | `Directory.CreateDirectory(dir)` | `XDG_DATA_HOME` or LocalApplicationData | Safe — tool data directory |
| Doctor.Core/Quarantine.cs:112 | `File.Move(origem, destino)` (seam `_move`) | source = validated L0 entry; destination = opaque `payload/NNNN.dat` | Safe — internally generated destination, sequential name unrelated to original (ADR-0010 §1) |
| Doctor.Core/Quarantine.cs:253 | `File.Move(tmpPath, manifestStaging)` | `.tmp` → `manifest.json` inside staging | Safe — internal atomic write, `overwrite: false` |
| Doctor.Core/Quarantine.cs:257 | `Directory.Move(stagingDir, finalDir)` | `staging-<guid>` → deterministic `<op_id>` | Safe — internal publish rename |
| Doctor.Core/Quarantine.cs:384 | `File.Move(payloadAbsoluto, destino, overwrite: false)` | quarantine payload → `original_path` from manifest | See Note A |
| Doctor.Core/Quarantine.cs:489 | `File.Move(tmp, manifestPath, overwrite: true)` | `.restore.tmp` → operation manifest | Safe — target is tool metadata, never user content; justification logged in code comment itself |

CLI (`ScanCommand.Run`) executes no mutant operations: only reads (enumeration + read-only-stream hash) and writes JSON report to memory before stdout. GUI contains no file I/O calls (empty scan for `File.*`, `Directory.*`, `FileStream`, `Process.` in src/Doctor.Gui).

**Item 1: PASS.**

## 2. Checklist 2 — All File Access via IStreamSource

Full content read chain mapped:

1. Single contract: `IStreamSource.OpenRead(FileEntry)` — Hashing.cs:28, read-only by contract.
2. Production implementations:
   - Doctor.Cli/FileStreamSource.cs:13 — `File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read)`: read-only, read sharing, no write.
   - Doctor.Core/PlaceholderGate.cs:90 — mandatory decorator reclassifying each open before delegating.
3. Pipeline content consumers:
   - L2/L3 via `Blake3Hasher` — production uses `PlaceholderGuardedHasher(new Blake3Hasher(), ...)` (ScanCommand.cs:122) and L3 exclusively uses `Blake3Hasher.FullHashBlake3(_gate, entry, ct)` (ScanPipeline.cs:172-175), which opens **only** through gate.
   - Pipeline without stream source (test doubles only) receives `RejectingStreamSource`, which throws on any open (ScanPipeline.cs:197-201) — impossible to read outside gate even in tests.
4. Opens outside IStreamSource found and their nature:
   - Quarantine.cs:111 (`File.OpenRead`) — injectable seam; reads **payload already inside quarantine** (file moved by tool itself, pre-validated against placeholder at Quarantine.cs:176-180). Not user content read in decision flow.
   - Quarantine.cs:333, 448 (`File.ReadAllBytes`) — JSON manifest read, tool metadata.
   - SidecarPlaceholderEnumerator.cs:63 (`File.ReadAllText`) — `.placeholder-meta.json` sidecar read, T04 convention metadata; parse failure falls to conservative `ReparsePoint` ("don't touch"), fail-closed.
   - Blake3Hasher.cs:53 (`File.OpenRead`) — test seam default; in production the public path always passes through `PlaceholderGuardedHasher`.

No user content opens outside IStreamSource/gate chain. Streams always disposed (`using`), buffers returned to `ArrayPool` in `finally` block.

**Item 2: PASS.**

## 3. Checklist 3 — PlaceholderGate Without Bypass

Decision points where placeholder is reclassified (double/triple gate):

| Layer | File:Line | Gate |
|---|---|---|
| Post-L0, safe-flow separation | PlaceholderGate.cs:104-132 (`Enforce`) | L0 flag OR `PlaceholderPolicy.IsPlaceholder` |
| Input telemetry | PlaceholderGate.cs:108-109 | rejects pre-existing `placeholder_bytes_read != 0` |
| Every stream open | PlaceholderGate.cs:135-141 (`OpenRead`) | same dual classification, throws BEFORE touching source |
| Decorated hasher | PlaceholderGuardedHasher.Enforce (PlaceholderGate.cs:68-75) | same, in both hash methods |
| L2/L3 decision point | ScanPipeline.HashGuarded (ScanPipeline.cs:182-190) | third defensive reclassification |
| Base hasher | Blake3Hasher.GatePlaceholder (Blake3Hasher.cs:106-113) | fourth barrier (direct flag) |
| Quarantine, pre-move | Quarantine.cs:176-180 | throws `PlaceholderViolationException` if item marked or classifiable |

Searched for bypass vectors: non-standard `IStreamSource` implementation (none beyond FileStreamSource and RejectingStreamSource), direct `FileStreamSource` consumption without gate (only reference is ScanCommand.cs:123, which hands source to pipeline — gate sits between it and any open), content read in enumeration (CrossPlatformEnumerator/Sidecar enumerate metadata, no content opens), "washed" telemetry (gate rejects violated counter at entry). No production path reads placeholder byte; the `placeholder_bytes_read == 0` invariant is maintained by construction across all layers. Existing dedicated tests: PlaceholderGateTests, PlaceholderGuardedHasherTests, PlaceholderPipelineIntegrationTests, Blake3HasherTests.

**Item 3: PASS.**

## 4. Checklist 4 — Installer (winget/MSIX, Code Signing, UAC)

Factual state: **no installer exists in the repository** — no packaging project, no MSIX/signing properties in .csproj files, no winget manifest, no signing script. It is declared roadmap: EPIC 14 — Packaging & Signing (SPEC §25/§28), README line 78 (GATE 6), test-strategy.md line 422 (GATE 5 requires installer review "when it exists").

Checklist recorded as requirement for future packaging card:

1. Format: MSIX as primary format (Microsoft Store path is high priority in SPEC §28) + standalone CLI distribution via GitHub Releases and MSP/RMM deployment (the EXE is the product, CANALS.md).
2. Winget manifest prepared after primary channel stabilization (SPEC §973).
3. Code signing: organization certificate (EV preferred for SmartScreen), sign binary AND installer; signing ADR pending per docs/reconnaissance.md.
4. UAC: product runs as regular user, no elevation (threat-model.md line 26) and never requires elevation to complete (line 173) — installer must install per-user (no elevation) or elevate only the copy step, never register app to require admin at runtime; `requestedExecutionLevel` asInvoker in app manifest.
5. No network/telemetry in installer (§26 consistent with product).

**Item 4: N/A in current state (nothing to audit) — requirements recorded. Non-blocking: corresponding gate (GATE 5/6) applies only when installer exists.**

## Observations (Non-Blocking, for Future Resolution/Quarantine-GUI Cards)

- **Note A — restore containment:** `Restore` uses `original_path` from manifest as `File.Move` destination (Quarantine.cs:358, 384) with occupied-destination protection and validated BLAKE3 hash before move, but without path containment validation against `rootPath` (e.g. jail/symlink). Manifest is metadata written by tool itself and threat model does not list tampered manifest as in-scope threat (no network, local use); still, defense in depth is recommended when card exposes `Restore` to GUI: validate canonical destination prefix under root and reject traversal.
- **Note B — restore TOCTOU window:** between phase 1 (full pre-check) and phase 2 (moves), a destination may come into existence; `File.Move(overwrite: false)` fails closed in that case and manifest is not marked restored — safe behavior, logged only for operational awareness.
- **Note C — harmless dead code:** always-true exception filter `|| true` (Quarantine.cs:267-269) and conditional with identical branches `"completed" : "completed"` (Quarantine.cs:263). No functional effect; candidates for cosmetic cleanup by implementation team.

## Verdict

| # | Item | Result |
|---|---|---|
| 1 | Destructive APIs with user args | **PASS** (zero Delete/DeleteDirectory; Moves confined to internal paths) |
| 2 | File access via IStreamSource | **PASS** |
| 3 | PlaceholderGate without bypass | **PASS** |
| 4 | Installer checklist | **N/A** — non-existent by design at this stage (EPIC 14/GATE 6); requirements recorded |

**GLOBAL VERDICT: PASS.** No violation of anti-delete rules (ADR-0002), no placeholder gate bypass (SPEC §6), no network/process/reflection surface in src/ (additional scan: only `Environment.GetEnvironmentVariable("XDG_DATA_HOME")` in CacheStore.cs:66). Suite 541/541 green in reviewer's execution.
