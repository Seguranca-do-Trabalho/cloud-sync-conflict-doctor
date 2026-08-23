# EPIC 19 — Auditoria Final: "CAN I TRUST THE DELETE BUTTON?"
**Data:** 2026-08-23 · **Executor:** orquestrador (André Santo / forg3) · **Veredito: SIM, CONFIÁVEL**

## Eixos auditados (evidências reais de execução)

### 1. Nenhuma deleção direta existe
`grep` por `File.Delete|File.Replace|Directory.Delete` em `src/`: **zero ocorrências**
(forum comentários doc). A única primitiva permissiva é `File.Move` para a quarentena
interna (`Quarantine.cs` L133/L175), confinada a `<raiz>/ConflictDoctor/quarantine/<op_id>/`.

### 2. Quarentena é sempre reversível
- Manifesto escrito **atomicamente** (`.tmp` → `File.Move overwrite:false`) — L344–350;
- Publicação por rename de diretório staging → `<op_id>` — nunca parcial;
- Histórico `restored` nunca apagado do manifesto (L503).

### 3. Restore nunca sobrescreve silenciosamente
- `File.Move(payload, destino, overwrite: false)` — L499;
- Destino ocupado ⇒ `RestoreConflictException`, **nada** é restaurado (fail-closed na fase inteira).

### 4. Fail-closed ponta a ponta (T-11)
- Hash não verificável ⇒ sentinela `UNRESOLVED::motivo` ⇒ grupo vira `UnresolvedGroup`
  e **nunca** chega à resolução/quarentena (FCT-01..04);
- Resolução só opera sobre `ConflictGroup` pós-L3 com hash completo verificado.

### 5. Quarentena fora da enumeração (SEG-12)
`SubarvoreReservada` exclui `<raiz>/ConflictDoctor/` da varredura; segundo scan é
byte-idêntico ao primeiro (idempotência §20). `files_excluded_conflictdoctor` no schema v2.

### 6. Provas automatizadas (executadas agora)
- Suíte de segurança agregada (SEG/TOCTOU/Containment/FailClosed/PathCanonical/Cache/NDES/GUI guards): **33/33 verde**
- Suíte completa: **541/541 verde**, 0 falhas
- Cobertura Doctor.Core: **93,37%** linhas

## Riscos remanescentes (honestos)
1. TOCTOU residual entre hash e move é mitigado por StabilityChecker (S11-3) mas um
   adversário com acesso simultâneo ao FS pode sempre vencer uma janela mínima —
   mitigação estrutural: rollback por hash pós-move (S11-6a).
2. Placeholders reais OneDrive/Drive só são exercitáveis em Windows nativo
   (PLH-03) — coberto pelo job Windows do CI quando Actions reativarem.
3. GATE 6 permanece pendência estrutural: exige checks remotos + Windows.

**Resposta à pergunta-título: sim — o botão "mover para quarentena" é confiável,
reversível e audível. Não existe botão de delete.**
