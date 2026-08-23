# Auditoria GATE 2 / GATE 6 — EPIC 13.5
# Data: 2026-08-23
# Task: t_9d93e373

## Ambiente
- Repo: /home/ubuntu/Projetos/Software/cloud-sync-conflict-doctor (branch main)
- .NET SDK: 8.0.x (~/dotnet)
- Build: dotnet build CloudSyncConflictDoctor.sln -c Release -> 0 warnings, 0 errors

## Execução dos testes (OS!=Windows)
```
dotnet test tests/Doctor.Tests -c Release --filter "OS!=Windows"
Passed! - Failed: 0, Passed: 534, Skipped: 0, Total: 534
```

## Cobertura (coverlet + runsettings threshold 0.85)
```
line-rate="0.8692" lines-covered="2234" lines-valid="2570"
```
- Coverage geral: 86.9% (>= 85% threshold) -> PASS

## Categorias por Gate 2 (docs/test-strategy.md §8)

| Categoria | Testes | Status |
|-----------|--------|--------|
| Determinism (DET-01..06) | 6/6 | VERDE |
| Placeholder (PLH-01..04) | 49/49 | VERDE |
| Grouping (GRP-01..03) | 19/19 | VERDE |
| Hashing (HSH-01..04) | 14/14 | VERDE |
| Cache (CACHE-01..04) | 19/19 | VERDE |
| Report (RPT-01..03) | 18/18 | VERDE |
| Safety (NDES-01..05) | 7/7 | VERDE |
| GuiVm (GUIVM-01..02) | 2/2 | VERDE |
| Security (SEG-*) | 11/11 | VERDE |
| Comparison | ~45/45 | VERDE |

## GATE 2 — Veredito
- Categorias do escopo deste EPIC (DET, PLH, Safety, GuiVm): TODAS VERDES localmente
- Cobertura Doctor.Core >= 85%: SIM (86.9%)
- Pendente: job Windows do CI (não executável neste host Linux)
- Classificação: **EVIDENCIA LOCAL COMPLETA (PENDENTE JOB WINDOWS)**

## GATE 6 — Veredito
- Exige: checks obrigatórios do GitHub Actions + suites nativas Windows + golden determinismo nos dois SO
- Status: SEM remote/push configurado, workflow não executa no GitHub Actions
- Classificação: **PENDENTE ESTRUTURAL** (depende de EPICs externos + remote/push/branch protection)

## Lacunas classificadas
1. GATE 2 job Windows -> PENDENTE ESTRUTURAL (CI remoto)
2. GATE 6 -> PENDENTE ESTRUTURAL (requer remote/push + branch protection)
3. GRP/HSH/CACHE/RPT -> PERTENCEM A EPICs 05-08, LISTAR COMO PENDENTE EXTERNO para este EPIC

## Notas
- Teste SecuritySeg12Tests.Security_QuarantineInsideScannedRoot_ExcludedFromEnumeration passou em 3 execuções consecutivas (antes falhava intermitentemente)
- Determinism: 6 testes DET-01..06 verdes com CDD_DETERMINISM_DIR funcionando
- Placeholder: 49 testes incluindo PlaceholderSuiteTests (PLH-01..04)
