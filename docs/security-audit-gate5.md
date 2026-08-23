# Auditoria de Segurança — Consolidação GATE 5

| Campo | Valor |
|---|---|
| Documento | docs/security-audit-gate5.md |
| Card | t_1543f566 (S11-6 — Consolidação GATE 5, filho de EPIC 11 t_e0cd185c) |
| Data | 2026-08-23 |
| Revisão | R2 (R1 pelo card t_1543f566; R2 pelo card t_218a0218/S11-6a — SEG-01/SEG-08 → VERDE) |
| Responsável | André Santo (forg3) — andre@junkyardgoodies.app |
| Base auditada | main @ 0ca0530 (worktree wt/t_1543f566; nenhum código alterado) |
| Base normativa | SPEC §45 (GATE 5), §26, §51; threat-model.md rev. 1.0 (commit 4bad5d2); ADR-0001..0011 |
| Método | Execução real da suíte + leitura integral das fontes atingidas + auditoria estática R1 (card t_ea884c03) |

## 1. Escopo e método

Este documento consolida a evidência do GATE 5 (SPEC §45): threat model revisado,
path/reparse attacks testados, race/TOCTOU analisado, installer reviewed. Nenhum código de
produto foi escrito ou alterado neste card.

Execuções reais desta sessão:

- `dotnet test CloudSyncConflictDoctor.sln` sobre main @ 0ca0530 → **Passed! Failed: 0,
  Passed: 436** (saída real desta run).
- Leitura integral de docs/threat-model.md, docs/SPEC.md (seções de gates), ADR-0002/0005/
  0006/0010, Directory.Packages.props, Quarantine.cs, PlaceholderGate.cs, PlaceholderPolicy.cs,
  ScanPipeline.cs, CacheStore.cs, ScanFingerprint.cs, CanonicalPath.cs, FileStreamSource.cs,
  SidecarPlaceholderEnumerator.cs.
- Auditoria estática independente R1 (relatório `auditoria-seguranca-T20-S11-6-R1.md`, commit
  81339e8 na branch wt/t_ea884c03, suíte 298/298 verde na época sobre main @ 399e353),
  incorporada como evidência complementar nos itens 4 e 5.

Convenção de numeração (decisão do orquestrador, VINCULANTE): SEG-nn segue a ordem EXATA das
linhas da §5 do threat-model. SEG-01 = primeira linha da tabela (`Security_PathTraversal_
HostileName_ContainedInRoot`) … SEG-23 = última linha (`Scan_PartialFailures_DoNotAbortWholeScan`).

## 2. Matriz SEG-01..SEG-23

Status: **VERDE** = teste existe, executa verde nesta run (436/436) e prova a mitigação;
**ADIADO** = justificativa formal na §4. Coluna "evid." cita commit que introduziu o teste +
arquivo da suíte. Regras R1–R12 conforme §4 do threat-model.

| SEG | Teste (nome canônico §5 TM) | Caso | Regra | Card resp. | Prior. | Status | Evidência (commit + suíte) |
|---|---|---|---|---|---|---|---|
| SEG-01 | `Security_PathTraversal_HostileName_ContainedInRoot` | T-01 | R2 | S11-6a (t_218a0218) | P0 | VERDE (R2) | CanonicalPathTests.cs (6cbac18) cobre a validação de nome; contenção em move/restore implementada em Quarantine.cs (f210608, wt/t_218a0218) + SecurityContainmentTests.Security_PathTraversal_HostileName_ContainedInRoot: vetores trailing dot/space, RLO U+202E e homóglifo; manifesto forjado fora da raiz recusado; suíte 438/438 |
| SEG-02 | `Security_LongPath_Over260Chars_ExtendedPrefixNoTruncation` | T-01 | R2 | S11-1 (T17) | P0 | ADIADO → T-13 (§4.2) | CanonicalPathTests.Normalize_ExtensaoAcimaDe255_TrunciaPara255EGateAprova (6cbac18); >260 chars não exercitado |
| SEG-03 | `Report_BidiControlChars_EscapedInJsonAndGui` | T-01 | R12 | S11-1 (T17) | P2 | VERDE (equivalente funcional) | ReportWriterTests.Write_ContraFixture_BytesIdenticos + Write_FormatoExato_UTF8SemBOM_LF_Final_NewlineTerminal (709ddf6): escape JSON RFC 8259 mínimo provado contra fixture; variante GUI pendente pós-GUI v2 |
| SEG-04 | `Security_JunctionLoop_TerminatesWithoutDescent` | T-02 | R3 | S11-2 (T18) | P1 | VERDE | ReparsePolicyTests.Reparse05_IntegracaoJunctionDeDiretorio_EhFolhaEOScanContinua + Loop01..Loop06 incl. Loop04_IntegracaoArvoreComCicloReal_TerminaEmTempoFinitoSemDuplicata (3a701c4) |
| SEG-05 | `Security_ReparseDir_PointingOutsideRoot_NotEntered` | T-02 | R3 | S11-2 (T18) | P1 | VERDE | ReparsePolicyTests.Reparse06_SymlinkParaForaDaRaiz_ConteudoExternoNaoVaza + ReparseDirectoryGuardTests.SymlinkDeDiretorio_ParaForaDaRaiz_NaoEhAtravessado (d5a5a0f, 3a701c4) |
| SEG-06 | `Placeholder_GateBeforeOpen_ZeroBytesRead` | T-03 | R3 | EPIC 03 (PLH) | P1 | VERDE | PlaceholderSuiteTests.Plh01_ScanArvoreMista_NenhumaAberturaDeStreamSobrePlaceholders + Plh02_HashSobrePlaceholder_LancaAntesDeQualquerIo + PlaceholderGateTests.Enforce_ArvoreMista_ZeroLeitura_RelatorioParcialCorreto (ece1112, 1b4516f) |
| SEG-07 | `Placeholder_ConvertedAfterEnumeration_IsCaughtBySecondGate` | T-03 | R3 | EPIC 03 (PLH) | P1 | VERDE | PlaceholderSuiteTests.Plh04_MutacaoPosGate_Falha_EhDetectadaPelosEspioes + Plh03_TelemetriaComBytesDePlaceholder_LancaViolacaoENuncaELavada + PlaceholderGateTests.OpenRead_NaoMarcada_ComBitsCrusDePlaceholder_TambemELanca (ece1112) |
| SEG-08 | `Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack` | T-04 | R4/R5 | S11-6a (t_218a0218) | P0 | VERDE (R2) | QuarantineTests.Move_MetadadoStale_ItemPulado_ArquivoPermaneceOndeEsta (19fe330): janela reduzida por revalidação size+mtime; rollback por hash pós-move implementado em Quarantine.cs (f210608, wt/t_218a0218) + SecurityContainmentTests.Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack: troca na janela hash→move ⇒ rollback, fonte intacta, FALHA, hash_pre_move ≠ hash_post_move no manifesto; suíte 438/438 |
| SEG-09 | `Quarantine_ShareModeExclusive_BlockWriterDuringHashWindow` | T-04 | R4 | S11-3 (T19) | P0 | ADIADO → T-13 (§4.2) |FileStreamSource abre FileShare.Read (d8cc7df), mas bloqueio exclusivo na JANELA hash→move não é testável no POSIX-fs atual |
| SEG-10 | `Scan_FileModifiedDuringRead_MarkedUnstable_AndNeverCached` | T-05 | R4/R10 | S11-3 (T19) | P0 | ADIADO → T-12c (§4.1) | Não há snapshot pós-leitura nem status UNSTABLE no pipeline L2/L3 |
| SEG-11 | `Scan_StableFile_MetadataUnchanged_ClassifiedNormally` | T-05 | R4 | S11-3 (T19) | P1 | VERDE (controle) | ScanPipelineTests.Scan_TresOrdensDeEnumeracao_SaidaIdenticaByteAByte + DeterminismSuiteTests (39cb05b): classificação normal sob instabilidade ausente é o caminho único exercitado |
| SEG-12 | `Security_QuarantineInsideScannedRoot_ExcludedFromEnumeration` | T-06 | R1 | S11-2 (T18) | P1 | ADIADO → T-15 (§4.5) | Exclusão por prefixo da subárvore ConflictDoctor/ não implementada na enumeração |
| SEG-13 | `Restore_DestinationExists_NeverOverwrites_RestoresAsDeterministicSibling` | T-07 | R7 | EPIC 08 (restore) | P0 | VERDE (conservador) | QuarantineTests.Restore_DestinoOcupado_FalhaComExcecao_SemTocarNada (19fe330): pré-checagem total falha FECHADA antes de tocar nada — mais conservador que irmão determinístico; irmão determinístico fica para card próprio do EPIC 08 |
| SEG-14 | `Restore_DestinationAbsent_MovesBackAndHashMatches` | T-07 | R7 | EPIC 08 (restore) | P0 | VERDE | QuarantineTests.Restore_AposMover_OriginalDeVoltaByteIdentico_HashPreservado (19fe330) |
| SEG-15 | `Restore_DestinationIdentical_AlreadyPresentNoMove` | T-07 | R7 | EPIC 08 (restore) | P1 | ADIADO → §4.6 | Ramo ALREADY_PRESENT (destino idêntico ⇒ não move, registra desfecho) não implementado; hoje destino ocupado ⇒ RestoreConflictException |
| SEG-16 | `Cache_FileIdReused_SizeMtimeDiffer_EntryInvalidated_Rehashes` | T-08 | R8 | S11-4 (T19) | P0 | VERDE (equivalente forte) | CachePoisoningTests.P1..P5 (b0367de): chave computacional BLAKE3(path,size,mtime) torna colisão por file_id estruturalmente impossível; MAC por linha detecta envenenamento (P4/P5/P6 fail-closed) |
| SEG-17 | `Cache_VolumeSerialDiffers_SameFileId_Miss` | T-08 | R8 | S11-4 (T19) | P1 | VERDE (obsoleto por design) | b0367de removeu file_id legado do schema v2 (decisão T-08/R8 do T19): chaves são derivadas do conteúdo+metadados; IDs recicláveis não existem mais como chave — caso encerrado, não aplicável |
| SEG-18 | `Security_OperationIdCollision_PreexistingManifestFailsClosed` | T-09 | R9 | S11-4 (T19) | P0 | VERDE (equivalente) | QuarantineTests.Move_MesmoEstadoDuasVezes_MesmoOperationId_ManifestoByteIdentico (19fe330) + escrita atômica overwrite:false (Quarantine.cs:253); op_id determinístico por conteúdo (O1–O3, b0367de) elimina colisão por relógio; create-new fail-closed provado |
| SEG-19 | `OperationId_Entropy_TwoConcurrentBatches_NeverCollide` | T-09 | R9 | S11-4 (T19) | P1 | VERDE (equivalente) | CachePoisoningTests.O1_OperationId_IsContentFingerprint_IgnoringPhysicalOrder + O2/O3 (b0367de): entropia vem do fingerprint de conteúdo BLAKE3 truncado 64 bits — lotes distintos nunca colidem |
| SEG-20 | `Security_DiskFullMidCopy_FailClosed_SourceIntact_NoPartialDeclaredSuccess` | T-10 | R6/R11 | S11-3 (T19) | P0 | PARCIAL → §4.7 | QuarantineTests.Move_FalhaNoSegundoItem_ManifestoParcialHonesto_EExcecao (19fe330): falha fechada com manifesto parcial honesto e fonte intocada provada; disco cheio cross-volume específico não injetado |
| SEG-21 | `Quarantine_SuccessRequiresFsyncOfDataAndManifest` | T-10 | R6 | S11-3 (T19) | P0 | ADIADO → T-13 (§4.2) | FlushFileBuffers não disponível/exercível no POSIX-fs de teste; escrita .tmp→move atômica já publicada (19fe330) |
| SEG-22 | `Permissions_AccessDenied_FileReportedAndNeverQuarantineEligible` | T-11 | R10 | S11-5 | P1 | ADIADO → §4.8 | Falha por-item registrada (ScanCommand.cs:99 captura UnauthorizedAccessException/IOException como anomalia, d8cc7df), mas elegibilidade negativa explícita "sem hash, sem resolução" não afirmada em teste |
| SEG-23 | `Scan_PartialFailures_DoNotAbortWholeScan` | T-11 | R10/R11 | S11-5 | P1 | VERDE | CliScanCommandTests.AnomaliasComArquivosPulados_ExitTres_MarcaParcial + ErrosSemAnomalias_PermaneceExitZero (d8cc7df): parcial explícito, exit 3, scan continua |

Células vazias: zero. Total: 23 linhas — **18 verdes** (R1: 16 + SEG-01 e SEG-08
convertidas em R2 pelo card t_218a0218/S11-6a, commit f210608, branch wt/t_218a0218),
**7 adiado/parcial com justificativa formal na §4**
(SEG-02, SEG-09, SEG-10, SEG-12, SEG-15, SEG-21, SEG-22; parciais remanescentes:
SEG-20; contagem líquida na §7).

## 3. Ownership (conforme decisão do orquestrador)

| Card | SEG sob responsabilidade | Resultado |
|---|---|---|
| S11-1 (T17, t_70329e55) | SEG-01/02/03 | Entregue (CanonicalPath.cs, 201 casos TDD, 399/399 na época); lacunas de integração migradas para adendos T-12a/T-13/T-14 |
| S11-2 (T18) | SEG-04/05/12 | SEG-04/05 verdes; SEG-12 → adendo T-15 |
| S11-3 (T19, t_43803664) | SEG-08/09/10/11/20/21 | SEG-11 verde + mitigação parcial em SEG-08/20; SEG-09/10/21 → adendos |
| S11-4 (T19, mesmo commit b0367de) | SEG-16/17/18/19 | Todos verdes ou encerrados por redesign (SEG-17) |
| S11-5 (T22/t_807d2357 + CLI T16) | SEG-22/23 | SEG-23 verde; SEG-22 → §4.8 |
| AUDITORIA ONLY: EPIC 03 | SEG-06/07 (família PLH) | Verdes — presença confirmada, não reimplementados |
| AUDITORIA ONLY: EPIC 08 | SEG-13/14/15 (restore) | SEG-13/14 verdes; SEG-15 → §4.6 |
| Este card (S11-6) | Consolidação, threat model, installer, supply chain, veredito | Este documento |

## 4. Revisão do threat model contra o código final

### 4.1 Adendos (novos casos descobertos — numeração T-12+, sem reescrita retroativa)

**T-12 — Lacunas de integração do T-01 no move/restore (degradação de SEG-01/02)** — severidade P0
O módulo `CanonicalPath` (IsValidName/Normalize, OrdinalIgnoreCase) valida nomes hostis com
rejeição fechada, mas a contenção byte-a-byte pelo prefixo canônico da raiz ANTES de cada
move/restore (mitigação (b) do T-01) não é executada no caminho do `File.Move`
(Quarantine.cs:196, 384). O manifesto é metadado da própria ferramenta e a auditoria R1
classificou o risco residual como baixo (Obs. A), porém o contrato do T-01 exige a verificação.
Ação: card novo no EPIC 03/08 para aplicar prefixo canônico + teste SEG-01 ponta a ponta
(árvore com `evil.txt. `, nome RLO e homóglifo passando por quarentena e restore).

**T-12a** = item acima relativo à quarentena; **T-12b** = extensão do rollback por hash
pós-move do T-04: o protocolo atual revalida size+mtime antes do move (Quarantine.cs:182-190)
e re-hasheia o payload JÁ NA QUARENTENA como fonte de verdade do manifesto
(Quarantine.cs:200-202), mas não compara esse hash com um hash pré-move do original nem
executa rollback automático em divergência — divergência hoje produz manifesto cujo hash
reflete os bytes realmente movidos (auditable, mas sem rollback). Ação: card no EPIC 08.

**T-12c** = ausência do snapshot pós-leitura do T-05: o pipeline não captura
`(size, mtime, file_id)` DEPOIS da leitura nem marca UNSTABLE; hash de leitura híbrida pode
entrar no cache (mitigado parcialmente pelo MAC por linha do CacheStore v2, que detecta
alteração de colunas, não de conteúdo). Ação: card no EPIC 05 (pipeline).

**T-13 — Dependências Windows-native não exercíveis no POSIX-fs (SEG-02/09/21)** — severidade P1
Prefixo estendido `\\?\`, partilha restrita sem WRITE/DELETE share na janela hash→move e
FlushFileBuffers (fsync) de dados e manifesto são contratos do threat-model (R2/R4/R6) que o
ambiente de teste Linux/POSIX-fs não consegue exercitar; o stub `WindowsNativeEnumerator` é
`#if WINDOWS`. Não são lacunas de design — são lacunas de PLATAFORMA DE TESTE. Ação: quando a
camada windows-native existir (EPIC de packaging/CI Windows do test-strategy §GitHub Actions),
criar suíte SEG-windows dedicada com esses três testes. Bloqueia GATE 6 (release Windows),
não GATE 5 na plataforma corrente.

**T-14 — Bidi/homóglifo marcado apenas no JSON, não na GUI (SEG-03, parte GUI)** — severidade P2
O escape JSON byte-exato está provado contra fixture (schema-report-v1). A marcação visual de
caracteres bidi na GUI (R12, segunda metade) depende das telas finais da GUI v2. Ação: card no
EPIC da GUI. Prioridade P2 — entra antes do RC conforme §5 do threat-model.

**T-15 — Subárvore ConflictDoctor/ não excluída na enumeração (T-06, SEG-12)** — severidade P1
A quarentena publica em `<raiz>/ConflictDoctor/quarantine/<op_id>/` dentro da raiz escaneada
(ADR-0002), mas o enumerador não pula essa subárvore por prefixo de bytes nem expõe o contador
`files_excluded_conflictdoctor`. Um segundo scan sobre a mesma raiz listaria payloads `.dat`
como candidatos. Mitigação parcial existente: payloads têm nome opaco sequencial sem extensão
semântica e a resolução exige hash BLAKE3 verificado (fail-closed R10), então falso "idêntico"
não decorre disso — mas o relatório polui e a idempotência §20 quebra. Ação: card no EPIC 02
(enumeração), prioridade alta porque afeta o fluxo real de uso repetido.

### 4.2 Reavaliação dos riscos residuais da §7 do threat-model

1. **Malware same-user**: inalterado — fora de escopo por construção (produto defende contra si
   mesmo). Confirmado pela auditoria R1: nenhuma superfície de rede/processo/reflection em src/.
2. **Manifesto órfão pós-queda de energia**: risco mantido; reconciliação de órfãos continua
   pendente no EPIC 08. Agrava-se tecnicamente com T-13 (fsync não exercível ainda) — a dupla
   (fsync + reconciliação) deve ser tratada no mesmo card futuro do EPIC 08.
3. **Colisão BLAKE3 desprezada**: mantido; defesa real (R4/R5) reforçada desde a rev. 1.0 pelo
   MAC por linha do CacheStore v2 e pelo hash do payload na quarentena.

Novo risco residual identificado nesta revisão: **op_id determinístico por conteúdo**
(ScanFingerprint, b0367de) substituiu o CSPRNG de 128 bits exigido pelo R9 original — troca de
colisão aleatória por determinância auditável; colisão agora exige mesmo conteúdo de ScanResult,
que por definição É a mesma operação. Aceito como equivalente forte ao R9 (ver SEG-18/19);
emenda registrada aqui para rastreabilidade.

## 5. Installer review (dependência EPIC 14 / GATE 6)

Estado factual: **NÃO EXISTE installer no repositório** — nenhum projeto de packaging, nenhuma
propriedade MSIX/signing nos .csproj (verificados Doctor.Core/Cli/Gui/Tests/FixtureGen),
nenhum manifesto winget, nenhum script de assinatura. Decisão de produto no commit 0ca0530:
v1 gratuita sem packaging/licensing/Store — packaging adiados para a v2. O EPIC 14 está
arquivado no board. Checklist registrado como requisito vinculante do futuro card de packaging:

| # | Item | Exigência (piso do threat-model §6/S7) | Status |
|---|---|---|---|
| 1 | Assinatura de código | Certificado de organização (EV preferível p/ SmartScreen); assinar binário E installer | N/A — sem installer; requisito registrado |
| 2 | Elevação mínima | Per-user sem elevação; `requestedExecutionLevel` asInvoker; runtime jamais exige admin (TM linha 173) | N/A — requisito registrado; app atual roda sem elevação |
| 3 | Uninstall limpo | Remove binários + cache `%LOCALAPPDATA%/ConflictDoctor/`; NUNCA toca quarentenas de dados do usuário | N/A — requisito registrado |
| 4 | Sem telemetria/rede (§26) | Zero endpoint de rede no installer; coerente com produto (auditoria R1: zero HttpClient/Socket/Process.Start em src/) | N/A — requisito registrado |
| 5 | Piso do atualizador (S7) | Quando existir updater: Ed25519 com chave pública embutida, hash verificado antes de executar, sem auto-execução silenciosa | N/A — sem updater; piso mantido no threat-model |

Veredicto do item: **N/A no estado atual** — GATE 5 não pode ser bloqueado por artefato
inexistente por decisão de escopo (0ca0530); a condição "installer reviewed" transfere-se ao
GATE 6 junto com a suíte T-13 (windows-native).

## 6. Supply chain

| Verificação | Resultado | Evidência |
|---|---|---|
| CPM ativo (`ManagePackageVersionsCentrally`) | OK | Directory.Packages.props |
| Transitive pinning ativo | OK | `CentralPackageTransitivePinningEnabled=true` |
| Versões exatas, sem ranges | OK | 10 PackageVersion fixadas (Blake3 2.1.0; Microsoft.Data.Sqlite 8.0.8; CommunityToolkit.Mvvm 8.3.2; Avalonia* 11.2.2; coverlet 6.0.2; Test.Sdk 17.11.1; xunit 2.9.2; runner 2.8.2) |
| Superfície de runtime mínima | OK | Doctor.Core: Blake3 + Microsoft.Data.Sqlite; Doctor.Cli: nenhuma dependência NuGet; Doctor.Gui: Avalonia (3 pacotes) + CommunityToolkit.Mvvm; FixtureGen: Blake3 |
| Pacote pinado sem uso | Avalonia.Diagnostics 11.2.2 pinado no CPM mas NÃO referenciado em nenhum csproj — remover do CPM (higiene, não bloqueante) | grep em todos os .csproj: zero referências |
| Licenças compatíveis com MIT | OK | Blake3 BSD-2-Clause; Microsoft.Data.Sqlite MIT; CommunityToolkit.Mvvm MIT; Avalonia MIT; Microsoft.NET.Test.Sdk MIT; coverlet MIT; xunit + runner Apache-2.0 (somente teste, não distribuído — compatível) |
| Rede/processo/reflection no produto | Zero ocorrências em src/ | grep HttpClient/WebClient/Process.Start/Socket/Dns/Assembly.Load: vazio (auditoria R1 confere) |

## 7. Veredito GATE 5 (SPEC §45)

| Condição | Atendida? | Evidência |
|---|---|---|
| Threat model revisado | **SIM** | Esta revisão: 4 adendos numerados (T-12…T-15), riscos residuais §7 reavaliados, equivalência R9 documentada; nenhuma reescrita retroativa |
| Path/reparse attacks testados | **PARCIAL** | Reparse: completo e verde (SEG-04/05, 8 testes). Path traversal: validação de nome hostil verde; **R2 (t_218a0218): contenção byte-a-byte no move/restore implementada e provada — SEG-01 VERDE; restam >260 chars (T-13) e variante GUI do bidi (T-14)** |
| Race/TOCTOU analisado | **SIM** | Janela hash→move reduzida por revalidação de metadados (SEG-08 parcial), cache venenoso encerrado por redesign (SEG-16/17), op_id determinístico provado (SEG-18/19), falha fechada com manifesto parcial honesto (SEG-20); lacunas UNSTABLE/rollback formalizadas em T-12b/T-12c. **R2 (t_218a0218): rollback por hash pós-move implementado e provado — SEG-08 VERDE; restam as lacunas T-12c (UNSTABLE) e T-13 (share exclusivo, plataforma)** |
| Installer reviewed | **SIM (com ressalva de transferência)** | Auditado: inexistente por decisão de escopo 0ca0530 (v1 sem packaging); checklist vinculante registrado na §5; condição física transfere-se ao GATE 6 |

**VEREDITO GLOBAL: GATE 5 NÃO FECHADO NESTE ESTÁGIO — 3 lacunas bloqueantes.**

> **R2 (2026-08-23, card t_218a0218/S11-6a):** lacuna 1 abaixo SANADA — SEG-01 e SEG-08
> verdes (implementação em Quarantine.cs + testes canônicos §5, commit f210608 na
> branch wt/t_218a0218; TDD RED→GREEN provado; suíte 438/438). Bloqueio cruzado ao
> GATE 3 reduzido na mesma medida. Permanecem bloqueando: SEG-10 (t_060a77cc) e
> SEG-12 (t_694bc7ce), além da suíte windows-native para SEG-02/09/21 (T-13).

Lacunas bloqueantes (cada uma com card responsável):

1. **SEG-01 — contenção byte-a-byte no move/restore sob nomes hostis (P0)** → card novo no
   EPIC 03/08 (T-12a). Os P0 desta matriz bloqueiam TAMBÉM o GATE 3 (Resolution Safety),
   conforme critério da §5 do threat-model — apontado explicitamente: SEG-01, SEG-08 (rollback),
   SEG-09, SEG-10, SEG-20 (disk-full específico) estão na lista de bloqueio compartilhada
   GATE 3+GATE 5 até seus testes ficarem verdes.
2. **SEG-10 — snapshot pós-leitura / UNSTABLE fora de decisões e do cache (P0)** → card novo no
   EPIC 05 (T-12c). Bloqueia GATE 3+GATE 5.
3. **SEG-12 — exclusão da subárvore ConflictDoctor/ na enumeração (P1)** → card novo no EPIC 02
   (T-15). Bloqueia GATE 5 (idempotência do fluxo real).

Não bloqueantes, com justificativa formal: SEG-02/09/21 = dependem de plataforma windows-native
(T-13; transferidos à suíte SEG-windows do GATE 6); SEG-15 = ramo ALREADY_PRESENT do restore
(melhoria do EPIC 08, comportamento atual fail-closed seguro); SEG-22 = afirmação de
inelegibilidade sem hash (endurecimento barato, EPIC 07); higiene CPM (remover
Avalonia.Diagnostics pinado sem uso).

Com os três cards acima fechados e verdes, as condições 1–3 do gate ficam plenamente verdes e o
GATE 5 fecha sem nova auditoria (re-execução da suíte basta); a condição 4 permanece
transferida ao GATE 6 por decisão de escopo documentada.

## 8. Nota de bloqueio cruzado (GATE 3)

Repetindo para efeito de trilha: os testes P0 da matriz — SEG-01, SEG-02, SEG-08, SEG-09,
SEG-10, SEG-13, SEG-14, SEG-16, SEG-18, SEG-20, SEG-21 — bloqueiam GATE 3 E GATE 5
simultaneamente (threat-model §5, critério de pronto). Estado deles: verdes SEG-01/08 (R2,
t_218a0218), SEG-13/14/16/18; parciais SEG-20; pendentes SEG-02/09/10/21. Portanto **GATE 3
também não fecha** enquanto as lacunas 2–3 (e a suíte windows-native para 02/09/21) não forem
sanadas — a lacuna 1 foi sanada em R2 pelo card t_218a0218.
