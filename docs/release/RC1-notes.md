# Release Candidate 1 — conflictdoctor (v1 free)

**Build:** `dotnet publish src/Doctor.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
**Artifact:** `dist/rc1-win-x64/conflictdoctor.exe` (67 MB, self-contained .NET 8)
**Date:** 2026-08-23 · **Author:** forg3

## Smoke Test Executed (Linux, dotnet run)
Tree with identical duplicate + unique file:
- v2 schema, BLAKE3, full telemetry (incl. `files_excluded_conflictdoctor`)
- Group correctly detected, exit code 0

## Main State in This RC
- 541/541 green tests · Doctor.Core coverage 93.37%
- Local CI green (build + suite + anti-delete/GUIVM/NDES guards)
- GATEs 1, 2 closed; GATE 4 baseline logged; GATE 5 audit consolidated
- EPIC 19: "CAN I TRUST THE DELETE BUTTON?" verdict = **YES**

## Known RC1 Limitations
- Windows CI job pending (Actions disabled this month) — PLH-03 runs only on Windows
- No code signing (deferred to paid v2 per owner decision)
- v1 distribution: GitHub Releases attachment, free
