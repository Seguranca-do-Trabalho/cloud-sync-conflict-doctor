# Release Candidate 1 — conflictdoctor (v1 gratuita)

**Build:** `dotnet publish src/Doctor.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
**Artefato:** `dist/rc1-win-x64/conflictdoctor.exe` (67 MB, self-contained .NET 8)
**Data:** 2026-08-23 · **Autor:** André Santo (forg3) | junkyardgoodies.app

## Smoke test executado (Linux, dotnet run)
Árvore com duplicata idêntica + arquivo único:
- schema v2, BLAKE3, telemetria completa (incl. `files_excluded_conflictdoctor`)
- grupo detectado corretamente, exit code 0

## Estado da main neste RC
- 541/541 testes verdes · cobertura Doctor.Core 93,37%
- CI local verde (build + suíte + guardas anti-delete/GUIVM/NDES)
- GATEs 1, 2 fechados; GATE 4 baseline registrado; GATE 5 auditoria consolidada
- EPIC 19: veredito "CAN I TRUST THE DELETE BUTTON?" = **SIM**

## Limitações conhecidas do RC1
- Job Windows do CI pendente (Actions desligadas no mês) — PLH-03 só roda em Windows
- Sem assinatura de código (adiada para a v2 paga por decisão do dono)
- Distribuição v1: anexo do GitHub Releases, gratuita
