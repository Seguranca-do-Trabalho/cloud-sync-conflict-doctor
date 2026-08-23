# Auditoria GATE 2 / GATE 6 — 2026-08-23 (EPIC 13.5)

Executor: orquestrador (André Santo / forg3). Todas as saídas abaixo são de execução real nesta máquina.

## Evidências coletadas

| Item | Comando | Resultado |
|---|---|---|
| Build Release | `dotnet build -c Release` | 0 erros |
| Suíte completa | `dotnet test -c Release --no-build` | **541/541 verde** |
| Categoria Determinism (trait) | `--filter Category=Determinism` | 6/6 ✓ |
| Categoria Safety (trait) | `--filter Category=Safety` | 7/7 ✓ |
| Categoria GuiVm (trait) | `--filter Category=GuiVm` | 2/2 ✓ |
| Suite DET-01..06 | FQN DeterminismSuite | 6/6 ✓ |
| Suite PLH-01..04 | FQN PlaceholderSuite | 4/4 ✓ |
| Cobertura Doctor.Core | coverlet + runsettings | **93,37%** linhas (3354/3592), branch 84,6% |
| CI YAML | inspeção `.github/workflows/ci.yml` | conforme: jobs linux+windows, gatilhos push/PR/cron noturno, traits OS=Windows filtradas no Linux |

## Veredito

### GATE 2 — Scanner Correctness
**EVIDÊNCIA LOCAL COMPLETA.** DET-01..06 todos presentes e verdes; PLH-01..04 verdes
(PLH-03 executa só em Windows — pendência do job Windows, não defeito). Cobertura
93,37% ≥ 85% exigido. GRP/HSH/CACHE/RPT: entregues pelos EPICs 05–08 e merged em main.

### GATE 6 — Product readiness
**PENDÊNCIA ESTRUTURAL:** exige checks obrigatórios do GitHub Actions (desligados
neste mês por decisão do dono) + suíte nativa Windows. Não é fechável localmente.
Classificação: PENDENTE ESTRUTURAL — nunca declarado fechado sem os dois SOs.

### Classificação de lacunas
- DEFEITO: nenhum (0 testes vermelhos no escopo)
- PENDENTE EXTERNO: job Windows do CI (Actions desligadas no mês)
- PENDENTE ESTRUTURAL: branch protection + checks remotos (GATE 6)
