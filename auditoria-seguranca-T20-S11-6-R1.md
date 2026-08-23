# Auditoria de Segurança + Installer Review — T20 (S11-6)

| Campo | Valor |
|---|---|
| Tarefa | t_ea884c03 — T20 - S11-6: Auditoria seguranca + installer review |
| Board | conflict-doctor |
| Data | 2026-08-23 |
| Revisão | R1 |
| Responsável | Worker revisor (ox-alpha), org Seguranca-do-Trabalho |
| Escopo | Análise estática de src/ (Doctor.Core, Doctor.Cli, Doctor.Gui) + verificação do estado do installer |
| Método | Grep dirigido por API de E/S + leitura integral dos arquivos atingidos + execução da suíte |
| Papel | REVISOR — nenhum código implementado ou commitado nesta tarefa |

## 0. Baseline verificada nesta execução

- Build + testes: `dotnet test CloudSyncConflictDoctor.sln` → **Passed! Failed: 0, Passed: 298** (executado pelo revisor, saída real).
- Base auditada: main @ 399e353 (merge T16).

## 1. Checklist 1 — APIs destrutivas com argumentos do usuário (proibido)

Padrões varridos em src/ (excl. bin/obj): `File.(Delete|Move|Replace|Copy|Open)`, `Directory.(Delete|CreateDirectory|Move)`, `DeleteDirectory`, `FileSystem.`.

Resultado agregado: **zero ocorrências de `File.Delete`, `Directory.Delete` ou `DeleteDirectory` em todo src/**. Ocorrências de APIs mutantes encontradas, classificadas uma a uma:

| Local | Chamada | Argumentos | Classificação |
|---|---|---|---|
| Doctor.Core/Quarantine.cs:152 | `Directory.CreateDirectory(quarantineRoot)` | `plan.RootPath` injetado + segmentos fixos `ConflictDoctor/quarantine` | Seguro — caminho interno do produto (SPEC §18), nunca argv cru |
| Doctor.Core/Quarantine.cs:160 | `Directory.CreateDirectory(payloadDir)` | staging sob quarantineRoot | Seguro — interno |
| Doctor.Core/Quarantine.cs:383 | `Directory.CreateDirectory(GetDirectoryName(destino))` | pai do `original_path` do manifesto | Ver Obs. A |
| Doctor.Core/CacheStore.cs:44 | `Directory.CreateDirectory(dir)` | `XDG_DATA_HOME` ou LocalApplicationData | Seguro — diretório de dados da ferramenta |
| Doctor.Core/Quarantine.cs:112 | `File.Move(origem, destino)` (seam `_move`) | origem = entrada L0 validada; destino = `payload/NNNN.dat` opaco | Seguro — destino gerado internamente, nome sequencial sem relação com o original (ADR-0010 §1) |
| Doctor.Core/Quarantine.cs:253 | `File.Move(tmpPath, manifestStaging)` | `.tmp` → `manifest.json` dentro do staging | Seguro — escrita atômica interna, `overwrite: false` |
| Doctor.Core/Quarantine.cs:257 | `Directory.Move(stagingDir, finalDir)` | `staging-<guid>` → `<op_id>` determinístico | Seguro — renomeação interna de publicação |
| Doctor.Core/Quarantine.cs:384 | `File.Move(payloadAbsoluto, destino, overwrite: false)` | payload da quarentena → `original_path` do manifesto | Ver Obs. A |
| Doctor.Core/Quarantine.cs:489 | `File.Move(tmp, manifestPath, overwrite: true)` | `.restore.tmp` → manifesto da operação | Seguro — alvo é metadado da ferramenta, nunca conteúdo do usuário; justificativa registrada no próprio comentário do código |

A CLI (`ScanCommand.Run`) não executa nenhuma operação mutante: apenas lê (enumeração + hash via stream somente-leitura) e escreve o relatório JSON em memória antes do stdout. A GUI não contém chamadas de E/S de arquivo (varredura vazia para `File.*`, `Directory.*`, `FileStream`, `Process.` em src/Doctor.Gui).

**Item 1: PASS.**

## 2. Checklist 2 — Todo acesso a arquivo via IStreamSource

Cadeia de leitura de conteúdo mapeada por completo:

1. Contrato único: `IStreamSource.OpenRead(FileEntry)` — Hashing.cs:28, somente-leitura por contrato.
2. Implementações de produção:
   - Doctor.Cli/FileStreamSource.cs:13 — `File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read)`: somente-leitura, compartilhamento de leitura, sem escrita.
   - Doctor.Core/PlaceholderGate.cs:90 — decorator obrigatório que reclassifica cada abertura antes de delegar.
3. Consumidores de conteúdo no pipeline:
   - L2/L3 via `Blake3Hasher` — produção usa `PlaceholderGuardedHasher(new Blake3Hasher(), ...)` (ScanCommand.cs:122) e L3 usa exclusivamente `Blake3Hasher.FullHashBlake3(_gate, entry, ct)` (ScanPipeline.cs:172-175), que abre **somente** através do gate.
   - Pipeline sem stream source (apenas doubles de teste) recebe `RejectingStreamSource`, que lança em qualquer abertura (ScanPipeline.cs:197-201) — impossível ler fora do gate mesmo em teste.
4. Aberturas fora do IStreamSource encontradas e sua natureza:
   - Quarantine.cs:111 (`File.OpenRead`) — seam injetável; lê **payload já dentro da quarentena** (arquivo movido pela própria ferramenta, pré-validado contra placeholder em Quarantine.cs:176-180). Não é leitura de conteúdo do usuário no fluxo de decisão.
   - Quarantine.cs:333, 448 (`File.ReadAllBytes`) — leitura do manifesto JSON, metadado da ferramenta.
   - SidecarPlaceholderEnumerator.cs:63 (`File.ReadAllText`) — leitura do sidecar `.placeholder-meta.json`, metadado da convenção T04; falha de parse cai no conservador `ReparsePoint` ("não tocar"), falha fechada.
   - Blake3Hasher.cs:53 (`File.OpenRead`) — default do seam de teste; em produção o caminho público sempre passa pelo `PlaceholderGuardedHasher`.

Nenhuma abertura de conteúdo de usuário fora da cadeia IStreamSource/gate. Streams são sempre descartadas (`using`), buffers devolvidos ao `ArrayPool` em bloco `finally`.

**Item 2: PASS.**

## 3. Checklist 3 — PlaceholderGate sem bypass

Pontos de decisão onde placeholder é reclassificado (duplo/triplo gate):

| Camada | Arquivo:linha | Gate |
|---|---|---|
| Pós-L0, separação do fluxo seguro | PlaceholderGate.cs:104-132 (`Enforce`) | flag L0 OU `PlaceholderPolicy.IsPlaceholder` |
| Telemetria de entrada | PlaceholderGate.cs:108-109 | recusa `placeholder_bytes_read != 0` pré-existente |
| Cada abertura de stream | PlaceholderGate.cs:135-141 (`OpenRead`) | mesma dupla classificação, lança ANTES de tocar a fonte |
| Hasher decorado | PlaceholderGuardedHasher.Enforce (PlaceholderGate.cs:68-75) | idem, nos dois métodos de hash |
| Ponto de decisão L2/L3 | ScanPipeline.HashGuarded (ScanPipeline.cs:182-190) | terceira reclassificação defensiva |
| Hasher base | Blake3Hasher.GatePlaceholder (Blake3Hasher.cs:106-113) | quarta barreira (flag direta) |
| Quarentena, pré-move | Quarantine.cs:176-180 | lança `PlaceholderViolationException` se item marcado ou classificável |

Procurados vetores de bypass: implementação de `IStreamSource` fora do padrão (nenhuma além de FileStreamSource e RejectingStreamSource), consumo direto de `FileStreamSource` sem gate (única referência é ScanCommand.cs:123, que entrega a fonte ao pipeline — o gate fica entre ela e qualquer abertura), leitura de conteúdo na enumeração (CrossPlatformEnumerator/Sidecar enumeram metadados, sem abrir conteúdo), telemetria "lavada" (gate recusa contador violado na entrada). Não há caminho de produção que leia byte de placeholder; a invariante `placeholder_bytes_read == 0` é mantida por construção em todas as camadas. Testes dedicados existentes: PlaceholderGateTests, PlaceholderGuardedHasherTests, PlaceholderPipelineIntegrationTests, Blake3HasherTests.

**Item 3: PASS.**

## 4. Checklist 4 — Installer (winget/MSIX, assinatura de código, UAC)

Estado factual: **não existe installer no repositório** — nenhum projeto de packaging, nenhuma propriedade MSIX/signing nos .csproj, nenhum manifesto winget, nenhum script de assinatura. É roadmap declarado: EPIC 14 — Packaging & Signing (SPEC §25/§28), README linha 78 (GATE 6), test-strategy.md linha 422 (GATE 5 exige revisão do installer "quando existir").

Checklist registrado como requisito do futuro card de packaging:

1. Formato: MSIX como formato primário (caminho Microsoft Store é prioridade alta na SPEC §28) + distribuição do CLI solto via GitHub Releases e implantação MSP/RMM (o EXE é o produto, CANAIS.md).
2. Manifesto winget preparado após estabilização do canal primário (SPEC §973).
3. Assinatura de código: certificado de organização (EV preferível para SmartScreen), assinar binário E installer; ADR de signing pendente conforme docs/reconhecimento.md.
4. UAC: o produto roda como usuário comum, sem elevação (threat-model.md linha 26) e jamais exige elevação para completar (linha 173) — o installer deve instalar per-user (sem elevação) ou elevar apenas o passo de cópia, nunca registrar o app para exigir admin em runtime; `requestedExecutionLevel` asInvoker no app manifest.
5. Sem rede/telemetria no installer (§26 coerente com o produto).

**Item 4: N/A no estado atual (nada a auditar) — requisitos registrados. Não bloqueante: o gate correspondente (GATE 5/6) só se aplica quando o installer existir.**

## Observações (não bloqueantes, para os cards futuros de resolução/quarentena-GUI)

- **Obs. A — contenção do restore:** `Restore` usa o `original_path` do manifesto como destino do `File.Move` (Quarantine.cs:358, 384) com proteção de destino ocupado e hash BLAKE3 validado antes de mover, mas sem validação de contenção do caminho contra `rootPath` (ex.: jail/symlink). O manifesto é metadado escrito pela própria ferramenta e o threat model não lista manifesto adulterado como ameaça em escopo (sem rede, uso local); ainda assim, recomenda-se defesa em profundidade no card que expuser `Restore` à GUI: validar prefixo canônico do destino sob a raiz e recusar traversal.
- **Obs. B — janela TOCTOU no restore:** entre a fase 1 (pré-checagem total) e a fase 2 (moves), um destino pode passar a existir; `File.Move(overwrite: false)` falha fechada nesse caso e o manifesto não é marcado restored — comportamento seguro, registrado apenas para consciência operacional.
- **Obs. C — código morto inofensivo:** filtro de exceção sempre-verdadeiro `|| true` (Quarantine.cs:267-269) e condicional com ramos idênticos `"completed" : "completed"` (Quarantine.cs:263). Sem efeito funcional; candidatas a limpeza cosmética pelo time de implementação.

## Veredito

| # | Item | Resultado |
|---|---|---|
| 1 | APIs destrutivas com args do usuário | **PASS** (zero Delete/DeleteDirectory; Moves confinados a caminhos internos) |
| 2 | Acesso a arquivo via IStreamSource | **PASS** |
| 3 | PlaceholderGate sem bypass | **PASS** |
| 4 | Checklist installer | **N/A** — inexistente por design neste estágio (EPIC 14/GATE 6); requisitos registrados |

**VEREDITO GLOBAL: PASS.** Nenhuma violação das regras anti-delete (ADR-0002), nenhum bypass do gate de placeholder (SPEC §6), nenhuma superfície de rede/processo/reflection em src/ (varredura adicional: apenas `Environment.GetEnvironmentVariable("XDG_DATA_HOME")` em CacheStore.cs:66). Suíte 298/298 verde na execução do revisor.
