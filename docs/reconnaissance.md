# Repository and Environment Reconnaissance — T00

| | |
|---|---|
| **Card** | t_76be6462 — T00 Repository reconnaissance |
| **Date** | 2026-08-22 |
| **Revision** | 1 |
| **Owner** | forg3 |
| **Scope** | Read-only and recording. No code files created or changed. |

Sources read in full in this session before any conclusion: `README.md`,
`docs/SPEC.md` (1822 lines), `docs/adr/ADR-0001.md`, `docs/adr/ADR-0002.md`,
`docs/adr/ADR-0003.md`. All cited commands were actually executed; the
summarized outputs below are what the shell returned.

---

## 1. State Found

### 1.1 Repository

- Git repository at `/home/ubuntu/Projetos/Software/cloud-sync-conflict-doctor`, branch `main`.
- Single commit in history: `d32effd` — "PHASE 0: specification baseline +
  ADRs 0001-0003 (language, quarantine, deterministic schema)".
- Working branch for this card: `wt/t_76be6462` (worktree at
  `.worktrees/t_76be6462`), currently at the same commit as `main`.
- **No remote configured** (`git remote -v` empty) — consistent with the
  instruction not to push.
- Tracked files in HEAD (total: 5):

```text
README.md
docs/SPEC.md
docs/adr/ADR-0001.md
docs/adr/ADR-0002.md
docs/adr/ADR-0003.md
```

- Untracked directory `.repowise/` present in worktree (tool index,
  not part of the product). Not added to commit.

### 1.2 .NET Solution Skeleton

**Does not exist.** Verified by `ls`: missing `CloudSyncConflictDoctor.sln`,
`src/`, and `tests/`. Recorded per card scope — no project was
created here.

### 1.3 Documentation

- `README.md`: complete product overview (problem, L0-L3 pipeline,
  per-media parallelism, file ID cache, risks, locked decisions).
- `docs/SPEC.md`: complete operational specification, including non-negotiable
  principles (§2 quarantine+restore, §3 byte-by-byte determinism,
  §6 untouched placeholders with `placeholder_bytes_read == 0`, §4 explicit
  BLAKE3, §26 zero telemetry/cloud, §52 anti-overengineering, §51
  priorities correctness > safety > determinism > data preservation >
  performance > UX), gates (§45), mandatory TDD (§19/§46) and PHASE 0 (§53).
- ADRs 0001–0003 decided: C#/.NET 8 stack with
  `CloudSyncConflictDoctor.sln` solution (`src/Doctor.Core`, `src/Doctor.Cli`,
  `src/Doctor.Gui`, `tests/Doctor.Tests`); quarantine+restore without direct
  delete; v1 report JSON schema with determinism rules.

---

## 2. Build Environment (Executed Commands)

| Item | Command | Result |
|---|---|---|
| .NET SDK | `dotnet --version` / `dotnet --list-sdks` | **8.0.424** at `/home/ubuntu/.dotnet/dotnet`; only installed SDK: 8.0.424 |
| Runtimes | `dotnet --list-runtimes` | NETCore.App 8.0.30; AspNetCore.App 8.0.30 |
| Git | `git --version` | 2.43.0 |
| OS | `/etc/os-release` | Ubuntu 24.04.4 LTS, kernel 6.17.0-1018-oracle |
| CPU/RAM | `nproc`, `free -h` | 4 cores, 23 GiB total (~19 GiB available) |
| Disk | `df -h` | `/`: 173 GiB available (11% used) |

Environment notes:

- SDK is at `~/.dotnet`; if the session PATH does not include it,
  `export PATH=$HOME/.dotnet:$PATH`.
- Local NuGet cache (`~/.nuget/packages`, 98 packages) already contains full xunit
  (`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` via
  `microsoft.net.test.sdk`) and win-x64 runtimes — inherited from SMB Speed Doctor.
  Does **not contain** `Blake3` or `Microsoft.Data.Sqlite`.
- NuGet connectivity verified:
  `curl https://api.nuget.org/v3/index.json` → HTTP 200. New package restore
  works.

### 2.1 Git config

`git config user.name` and `git config user.email` returned **empty**, in both
global and repository/worktree scope. Without configuration, commits come out
with invalid identity or inherit system values. Implementation cards
must configure per repository before the first commit:

```bash
git config user.name "forg3"
git config user.email "forg3"
```

(This card configured this in its own worktree for the document commit;
future cards need to repeat in theirs or set once in the main repo.)

---

## 3. Gaps Against PHASE 0 Checklist (SPEC §53)

| Dimension (§53) | Current State | Gap |
|---|---|---|
| Language | Decided (ADR-0001): C#/.NET 8. SDK 8.0.424 available | None in decision; missing project materialization |
| Build system | None — no `.sln`, no `.csproj` | Create solution and 4 projects (T01/T07) |
| Existing tests | None — no test project; xunit harness present in NuGet cache | Create `tests/Doctor.Tests` with TDD from first code commit |
| CI | Non-existent — no workflow/pipeline in repo | Define minimal CI (build + tests + determinism test) — depends on build existing |
| Directory structure | Only `docs/`; no `src/` or `tests/` | Create exact ADR-0001 tree |
| Documentation | README + SPEC + 3 complete ADRs | None blocking; remaining §25 ADRs (UI framework, scanner, hashing, SQLite, concurrency, cache, CLI, GUI, quarantine, comparator, packaging, updater, signing, licensing, telemetry, report schema) still pending in T01+ cards |
| Dependencies | None declared. Cache has xunit; missing `Blake3` and `Microsoft.Data.Sqlite` (NuGet accessible, HTTP 200) | Declare on first restore; pin explicit versions |

Summary: the repository is in the condition the SPEC predicted for
implementation start — documentation ready, code zero. The full gap is
materializing the skeleton solution and its infrastructure (build, tests, CI).

---

## 4. Environment Risks

1. **Linux host developing Windows-first product.** All Windows-native
   requirements (FindFirstFileEx, FILE_ATTRIBUTE_OFFLINE /
   RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS, reparse points, file IDs,
   IOCTL_STORAGE_QUERY_PROPERTY) are **untestable on this host**: NTFS
   placeholder attributes and Win32 P/Invoke do not exist here. Risk of false
   "ready" if tests run only on Linux. Mitigation: contracts isolated behind
   interfaces (already planned in ADR-0001), simulated placeholder tests by
   contract + dedicated suite on Windows runner (CI) before gates 2 and 5.
2. **Empty Git identity by default.** First commit of each worktree fails
   or comes out with wrong author if not configured. Cheap to fix, expensive if
   forgotten (dirty history).
3. **Missing native dependencies in cache.** `Blake3` (native binding) and
   `Microsoft.Data.Sqlite` require download on first restore. NuGet
   accessible today; risk only if environment goes offline.
4. **Single SDK (8.0.424).** Any card pinning `net8.0` works; an
   accidental TargetFramework upgrade to 9/10 would break the build here.
5. **`.repowise/` loose in worktree.** Untracked tool artifact;
   add to `.gitignore` in the card creating the skeleton to prevent leaking
   into future commits.
6. **Broken external references in README.** `../CANAIS.md` and
   `../PRICING.md` do not exist in repo (verified with `ls`). Either
   sibling documents outside the repository or pending; do not block T01-T07.

---

## 5. Immediate Recommendations for T01-T07

1. **T01 (architecture baseline)**: first technical act = `dotnet new sln` +
   the 4 ADR-0001 projects + `.gitignore` covering `bin/`, `obj/`,
   `.repowise/` + git identity config in main repo. Solution must
   compile with `dotnet build` and `dotnet test` green (even with zero or
   one canary test) before any logic.
2. **Pin explicit versions** of `Blake3` and `Microsoft.Data.Sqlite` in
   csproj on first use; never floating range — build determinism is
   extension of report determinism.
3. **T05 (test strategy)**: define now how Windows-only tests will be
   marked/filtered (e.g. trait `[Trait("Platform","Windows")]`) so Linux CI
   does not mask false placeholder/reparse coverage.
4. **T03 (report schema)**: start from ADR-0003 JSON; include
   `placeholder_bytes_read == 0` as tested invariant, not decorative field.
5. **T04 (benchmark harness)**: versioned dataset generator can be written
   and tested on Linux (synthetic tree generation is portable); only
   Windows-native I/O metrics need a Windows runner.
6. **Unlock order confirmed**: current state blocks nothing in T01-T07 in
   parallel after the skeleton; the only hard precedence is skeleton → everything
   else.
