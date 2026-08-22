# Threat Model — Cloud Sync Conflict Doctor

| Campo | Valor |
|---|---|
| Documento | docs/threat-model.md |
| Card | t_e1fcbbfa (T02 — Threat model, papel: security) |
| Data | 2026-08-22 |
| Revisão | 1.0 |
| Responsável | André Santo (forg3) — andre@junkyardgoodies.app |
| Base normativa | docs/SPEC.md §2, §3, §6, §12, §18, §22, §26, §45 (GATE 5), §51, §58; ADR-0001, ADR-0002, ADR-0003 |
| Status | Aceito como referência para GATE 5; casos T-01…T-11 são obrigatórios |

## 1. Escopo e método

Este documento modela o que pode **destruir dados do usuário** no Cloud Sync Conflict Doctor.
A pergunta central é a mesma da auditoria final da SPEC (§58): *pode apagar o arquivo errado,
pode perder um arquivo durante quarantine, pode restaurar para o lugar errado, pode corromper um arquivo?*
Cada caso abaixo é uma rota concreta até um "SIM" — e a mitigação que transforma o SIM em NÃO testado.

Método: análise por superfície de ataque, depois casos concretos com vetor, impacto, mitigação
obrigatória e o teste que prova a mitigação. Prioridade de decisão segue §51:
correctness > safety > determinism > data preservation > performance > UX.

Premissas do modelo de ameaça:

1. O produto roda como usuário comum, sem elevação, sem rede, sem telemetria (§26).
2. O adversário inclui: o próprio conteúdo da árvore (nomes hostis), clientes de sincronização
   concorrentes (OneDrive/Dropbox etc. mutando arquivos durante o scan), outros processos locais
   (incluindo malware rodando como o mesmo usuário) e erro humano do usuário.
3. O filesystem Windows é hostil por padrão: tudo entre o hash e o move pode mudar (TOCTOU é o
   estado normal, não a exceção).

Ativos a proteger, em ordem:

```text
A1. Conteúdo e existência dos arquivos do usuário (nada é apagado, corrompido ou sobrescrito)
A2. Posicionamento dos arquivos (restore volta ao lugar certo, nunca substitui trabalho novo)
A3. Correção do relatório (duas versões diferentes nunca classificadas como idênticas)
A4. Economia de placeholders (nenhum byte de placeholder lido, nenhuma hidratação provocada)
A5. Integridade dos manifestos (cadeia de evidência da quarentena ao restore)
A6. Privacidade local-first (nenhum dado sai da máquina)
```

## 2. Superfícies de ataque

| # | Superfície | Interação com dados | Fronteira de confiança | Risco principal |
|---|---|---|---|---|
| S1 | Scan (Level 0–3) | Leitura de metadados e conteúdo | Fora do controle: nomes de arquivos, atributos, clientes de sync concorrentes | Ler placeholder (A4); seguir reparse para fora da árvore (A6); instabilidade durante leitura corrompendo a classificação (A3) |
| S2 | Cache SQLite | Leitura/escrita de hashes por file_id | Arquivo local modificável pelo usuário/malware; IDs NTFS recicláveis | Envenenamento de cache → falso "idêntico" → quarentena de conteúdo único (A1, A3) |
| S3 | Quarentena (move) | Move/copy+delete controlado | Disco, volumes, ACLs, concorrência | Perda parcial ou total no meio do move; destino indevido por nome hostil (A1) |
| S4 | Restore (move de volta) | Move de volta ao caminho original | Estado da árvore mudou desde a quarentena | Sobrescrever arquivo novo do usuário (A1, A2) — pior cenário único do produto |
| S5 | CLI | Argumentos, stdout/JSON | Entrada do usuário e automação/RMM; nomes hostis vazando na saída | Injeção/truncamento de saída que engana automação; exit code ambíguo disparando ação errada |
| S6 | GUI | Apresenta relatório, dispara resolução | Conteúdo do relatório é dado hostil (nomes); usuário sob pressão | Botão destrutivo indistinguível de ação segura (§37); confirmação que não mostra caminho real |
| S7 | Atualizador (futuro) | Substitui o binário | Canal de distribuição | Update malicioso/não assinado = comprometimento total da máquina (supply chain) |

Princípio transversal: **o cache acelera, nunca decide sozinho**. Toda decisão que leva a
quarentena exige cadeia de evidência verificada na sessão (hash atual ou tripé de cache
estritamente validado — caso T-08).

## 3. Casos concretos

Severidade: **P0** = rota direta para perda/corrupção de dados do usuário; **P1** = violação de
invariante central (placeholder, privacidade, determinismo); **P2** = degradação confiável mas
sem perda direta.

### T-01 — Path traversal via nome de arquivo hostil — severidade P0

| Item | Detalhe |
|---|---|
| Vetor | Nome de arquivo criado via API `\\?\` (legal no NTFS, invisível ao Explorer): `arquivo.txt.` e `arquivo.txt ` (trailing dot/space — Win32 sem prefixo estendido resolve `arquivo.txt.`, ou seja, opera num arquivo DIFERENTE do exibido); caminhos >260 chars que truncam em APIs ANSI/legacy; Unicode hostil: U+202E (RTL override) tornando `fdp.exe` visível como `exe.pdf`, homóglifos cirílicos/latinos; nomes reservados (`CON`, `NUL`, `COM1`). |
| Exemplo | Quarentena recebe `move("C:\sync\arquivo.txt. ", dest)` montado por concatenação sem prefixo estendido → SO move `arquivo.txt` errado; relatório exibe nome falsificado por RLO e o usuário aprova quarentena do arquivo errado achando que é outro. |
| Impacto | Operação sobre arquivo diferente do pretendido (A1); usuário enganado na aprovação (A3); automação RMM consumindo JSON com nome truncado age errado. |
| Mitigação obrigatória | (a) Todo caminho canonizado uma única vez na entrada (`GetFullPathName`) e convertido para forma estendida `\\?\`; proibida concatenação crua de strings de caminho — sempre combinação estrutural + re-canonização. (b) Verificação de contenção byte-a-byte: o caminho resolvido de QUALQUER operação de escrita/move deve começar pelo prefixo canônico da raiz (quarentena) — senão a operação falha fechada. (c) Nomes preservados exatamente como o filesystem os dá (sem "correção" silenciosa de dots/spaces/reservados); caracteres de controle bidi marcados no relatório. (d) Agrupamento por `normalized_base_name` opera em bytes UTF-8 exatos — sem fold visual (homóglifo é outro nome). |
| Teste que prova | `Security_PathTraversal_HostileName_ContainedInRoot` — árvore com `evil.txt. `, nome RLO, nome com homóglifo; quarentena e restore executam e o prefixo de contenção é validado; nenhum caminho fora da raiz é tocado. Companheiros: `Security_LongPath_Over260Chars_ExtendedPrefixNoTruncation` (>260 chars, move e restore íntegros) e `Report_BidiControlChars_EscapedInJsonAndGui`. |

### T-02 — Junction/symlink loop na enumeração — severidade P1

| Item | Detalhe |
|---|---|
| Vetor | Junction `loop -> .` (auto-referência) ou ciclo `a -> b -> a`; symlink de diretório apontando para fora da raiz (`link -> C:\Users\outro`). Enumeração recursiva ingênua entra em ciclo infinito ou desce para fora da árvore. |
| Exemplo | Usuário tem junction legado de migration (`Documents and Settings`); scan entra em loop, cresce memória/log sem fim e, no caso do symlink externo, hash de arquivos de terceiros vaza para o relatório local. |
| Impacto | DoS do próprio scan (nunca termina); violação de privacidade local-first ao ler fora da raiz (A6); relatório não-determinístico dependendo de onde o ciclo é cortado. |
| Mitigação obrigatória | Diretório com `FILE_ATTRIBUTE_REPARSE_POINT` é SEMPRE folha: registra metadados (Level 0), nunca desce — vale para junction, symlink, mount point, qualquer alvo (SPEC §6 "não seguir links/reparse"). Defesa em profundidade: guarda de visitados por `(volume_serial, file_id)` e teto de profundidade configurável com registro no relatório. Nunca decidir pela ordem de visita (determinismo §3). |
| Teste que prova | `Security_JunctionLoop_TerminatesWithoutDescent` — árvore sintética com ciclo `a->b->a` termina em tempo finito e conta os reparse como folhas. Companheiro: `Security_ReparseDir_PointingOutsideRoot_NotEntered` (conteúdo externo nunca aparece no relatório). |

### T-03 — Reparse point disfarçado (placeholder que passa pelo gate) — severidade P1

| Item | Detalhe |
|---|---|
| Vetor | Placeholder cfapi (OneDrive/Files On-Demand) cujo atributo de recall só é visível com query correta; metadado em cache stale; arquivo que era regular e foi convertido em placeholder entre a enumeração e a abertura. Uma única leitura dispara hidratação: download gigabytes, custo de banda, alteração de comportamento do cliente de sync. |
| Exemplo | Vídeo de 8 GB online-only; bug de gate abre o arquivo; `placeholder_bytes_read` vai a 8 GiB e o OneDrive baixa tudo — exatamente o dano que o produto promete não causar (A4). |
| Impacto | Violação da invariante `placeholder_bytes_read == 0` (SPEC §6/§21); custo financeiro/material direto ao usuário; perda de confiança no produto. |
| Mitigação obrigatória | Duplo gate: (1) atributos vindos da enumeração Level 0; (2) re-verificação IMEDIATAMENTE antes de cada abertura, na mesma chamada de decisão; qualquer bit suspeito (`OFFLINE`, `RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS`, reparse) → classe PLACEHOLDER, zero leitura. Abertura de candidato nunca usa conveniência tipo `File.ReadAllBytes` sobre caminho — usa handle com flags explícitas; contadores `files_placeholder` e `placeholder_bytes_read` incrementados no ponto de abertura, e o teste afirma zero. |
| Teste que prova | `Placeholder_GateBeforeOpen_ZeroBytesRead` — árvore com os quatro tipos da SPEC §21; afirmação dura `placeholder_bytes_read == 0` e nenhuma função de hash chamada sobre placeholder (spy/mock na interface de hashing). Companheiro: `Placeholder_ConvertedAfterEnumeration_IsCaughtBySecondGate`. |

### T-04 — TOCTOU entre hash e move para quarentena — severidade P0

| Item | Detalhe |
|---|---|
| Vetor | Janela entre calcular o hash do original e concluir o move. Outro processo (usuário, sync client, malware) troca o conteúdo no caminho: renomeia arquivo novo para o lugar ou reescreve. O manifesto registra o hash H1, mas os bytes movidos são H2. No restore, o produto devolve H2 jurando ser H1 — corrupção semântica garantida. |
| Exemplo | `relatorio.docx` hashado como versão boa; Dropbox sincroniza versão nova no meio da janela; quarentena move a versão nova; restore futuro repõe a versão nova no lugar da boa registrada — e a "boa" se perdeu. |
| Impacto | Perda/corrupção de dados (A1) com aparência de procedimento correto — o pior tipo de falha para um produto cujo diferencial é confiança auditável. |
| Mitigação obrigatória | Encadear evidência, não confiar em janela: (1) abrir o candidato com partilha restrita (sem `FILE_SHARE_WRITE`, sem `FILE_SHARE_DELETE`) durante o hashing — bloqueia escritores e renomeadores durante a janela crítica; (2) mover; (3) **reabrir o arquivo NA QUARENTENA e recalcular BLAKE3**; (4) comparar com o hash pré-move; divergência → rollback (move de volta), operação marcada FALHA, nada declarado sucesso. Manifesto carrega `hash_pre_move` e `hash_post_move` — a cadeia de evidência é auditable depois. Sucesso só é declarado após (4) bater (caso T-10 completa com fsync). |
| Teste que prova | `Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack` — hook de teste injeta troca de conteúdo entre hash e move; afirma: rollback executado, fonte intacta, operação FALHA no índice, `hash_post_move != hash_pre_move` registrado. Companheiro: `Quarantine_ShareModeExclusive_BlockWriterDuringHashWindow` (segunda escrita concorrente falha durante a janela). |

### T-05 — Arquivo alterado durante o scan (hash não confere) — severidade P0

| Item | Detalhe |
|---|---|
| Vetor | Usuário edita/salva enquanto o scanner lê; cliente de sync substitui o arquivo no meio da leitura. Hash parcial/full computado sobre estado híbrido (metade velho, metade novo). |
| Exemplo | Planilha salva às 19:00:01; leitura começou 19:00:00; BLAKE3 resultante não corresponde a nenhuma versão real. Se esse hash entrar no cache (S2), o veneno persiste nos scans seguintes (combina com T-08). |
| Impacto | Classificação errada: duas versões distintas podem parecer idênticas (A3) → resolução quarentena conteúdo único (A1); ou idênticas parecem divergentes (ruído, menos grave). |
| Mitigação obrigatória | Snapshot de consistência por arquivo: capturar `(size, mtime, file_id)` ANTES da leitura, reler os três DEPOIS; qualquer mudança → arquivo marcado `UNSTABLE`, excluído de toda decisão de igualdade/divergência nesta sessão (re-enfileirado uma vez; persistindo, entra no relatório como instável). Hash de arquivo instável JAMAIS é gravado no cache. Regra fail-closed: dúvida sobre igualdade ⇒ tratar como divergência potencial, nunca como cópia segura para remover. |
| Teste que prova | `Scan_FileModifiedDuringRead_MarkedUnstable_AndNeverCached` — gravação concorrente simulada no meio da leitura; afirma: status UNSTABLE, fora de grupos idênticos, cache sem entrada para o arquivo. Companheiro: `Scan_StableFile_MetadataUnchanged_ClassifiedNormally` (controle positivo). |

### T-06 — Quarentena dentro da árvore escaneada (recursão/auto-engolir) — severidade P1

| Item | Detalhe |
|---|---|
| Vetor | ADR-0002 fixa a quarentena em `<raiz>/ConflictDoctor/quarantine/<timestamp>/` — dentro da pasta sincronizada. O próximo scan enumera as cópias em quarentena como arquivos vivos: duplicata aparece em dobro, e a resolução pode mandar para quarentena o conteúdo que JÁ ESTÁ em quarentena, empilhando lixo e poluindo manifestos. Cliente de sync ainda propaga `ConflictDoctor/` para outras máquinas, multiplicando o problema. |
| Exemplo | Scan de `C:\Users\eu\OneDrive`; resolução move 500 duplicatas; novo scan encontra 1000 candidatos (500 vivos + 500 em quarentena); usuário resolve de novo; 500 cópias fantasma viram candidatos permanentes. |
| Impacto | Relatório incorreto e não-idempotente (A3); risco de operações redundantes sobre conteúdo já protegido; crescimento infinito de candidatos fantasma. |
| Mitigação obrigatória | Exclusão estrutural na enumeração: subárvore `<raiz>/ConflictDoctor/` é pulada no Level 0 por comparação de PREFIXO EM BYTES do caminho canônico (barata, determinística), com contador próprio `files_excluded_conflictdoctor` na telemetria. Segunda camada: quarentena marca seus arquivos (atributo `HIDDEN` + registro no índice) e o scanner ignora qualquer candidato presente no índice de quarentena. Terceira camada: resolução se recusa a mover arquivo cujo caminho já esteja sob o diretório de quarentena. |
| Teste que prova | `Security_QuarantineInsideScannedRoot_ExcludedFromEnumeration` — resolve numa árvore, escaneia de novo; afirma: zero itens de `ConflictDoctor/` no relatório, `files_excluded_conflictdoctor > 0`, segundo scan byte-idêntico ao primeiro (idempotência §20). |

### T-07 — Restore sobrescrevendo arquivo novo — severidade P0

| Item | Detalhe |
|---|---|
| Vetor | Entre a quarentena e o restore, surge um arquivo no caminho original: usuário recriou, sync client trouxe versão de outra máquina, outra ferramenta escreveu lá. Restore ingênuo (`move` por cima) DESTRÓI o arquivo novo — perda permanente, sem quarentena, sem undo. É a única operação do produto que pode apagar dados sem passar pelo caminho de quarentena. |
| Exemplo | `orcamento.xlsx` conflituoso vai à quarentena; o cliente de sync restaura a versão do servidor no mesmo caminho no dia seguinte; usuário clica "restaurar"; versão do servidor é sobrescrita pela quarentenada e desaparece. |
| Impacto | Destruição irreversível de trabalho do usuário (A1, A2) — viola ADR-0002 §3 ("nunca sobrescrever") e a promessa central do produto. |
| Mitigação obrigatória | Restore NUNCA escreve sobre destino existente, sem exceção: (1) destino ausente → move de volta, verifica hash, registra SUCESSO; (2) destino existente com hash idêntico ao manifestado → não toca no existente, registra `ALREADY_PRESENT`, não move; (3) destino existente com conteúdo diferente → move o arquivo de quarentena para caminho-irmão determinístico `base (restaurado <operation_id-curto>).ext` (nunca substitui, nunca sufixo aleatório) OU falha pedindo decisão explícita — padrão conservador: irmão + aviso. Em todos os casos o manifesto original ganha registro de desfecho append-only. Comparação sempre por hash BLAKE3, nunca por mtime/tamanho sozinhos. |
| Teste que prova | `Restore_DestinationExists_NeverOverwrites_RestoresAsDeterministicSibling` — destino ocupado por conteúdo diferente; afirma: existente intocado byte-a-byte, quarentenado presente no irmão determinístico, desfecho registrado. Companheiros: `Restore_DestinationAbsent_MovesBackAndHashMatches` (caminho feliz) e `Restore_DestinationIdentical_AlreadyPresentNoMove`. |

### T-08 — Cache envenenado por reuso de file ID — severidade P0

| Item | Detalhe |
|---|---|
| Vetor | NTFS reutiliza file IDs após deleção. Cache chavado só por `file_id` (SPEC §12) devolve o hash do arquivo ANTIGO para um arquivo NOVO que herdou o ID. Dois conteúdos diferentes passam a compartilhar hash "em cache" → classificados como cópias idênticas. |
| Exemplo | `contrato_v1.docx` deletado; ID reciclado por `contrato_FINAL.docx` com conteúdo distinto; cache diz hash igual ao de `contrato_copia.docx`; o doctor declara "cópias idênticas"; usuário mantém uma e quarentena a outra — conteúdo único destruído com aval do produto. |
| Impacto | Falso "idêntico" é a rota mais barata para destruição de dados (A1, A3) e sobrevive a reinícios do processo (persistência do veneno no SQLite). |
| Mitigação obrigatória | Chave de validade composta: entrada de cache só é usada se `(volume_serial, file_id, size, mtime_ticks, algorithm, hash_version)` casarem EXATAMENTE com o estado atual do arquivo; qualquer divergência = cache miss → rehash completo do pipeline. `mtime` com precisão nativa máxima (ticks, não segundos). `volume_serial` acompanha todo file_id (IDs só são únicos por volume). Reforço: hash vindo de cache usado em decisão de resolução gera linha de auditoria própria no relatório (`evidence: cache` vs `evidence: fresh`). Cache nunca é autoridade sozinha (princípio §2). |
| Teste que prova | `Cache_FileIdReused_SizeMtimeDiffer_EntryInvalidated_Rehashes` — fixture simula reuso de ID com size/mtime diferentes; afirma: rehash executado, hash antigo não reaproveitado, classificação correta. Companheiro: `Cache_VolumeSerialDiffers_SameFileId_Miss` (mesmo ID em volumes distintos não colide). |

### T-09 — Colisão de operation_id — severidade P0

| Item | Detalhe |
|---|---|
| Vetor | Dois lotes no mesmo segundo (lote agendado + manual) geram o mesmo `operation_id` derivado de timestamp; o segundo manifesto SOBRESCREVE o primeiro. O primeiro lote perde sua cadeia de evidência: restore não sabe mais quais bytes correspondem a qual entrada — restauração ambígua sobre dados do usuário. |
| Exemplo | Lote 01h00m00s quarentena 300 arquivos; usuário dispara outro lote no mesmo segundo; manifesto do primeiro é substituído; restore em massa do dia anterior restaura entradas cruzadas. |
| Impacto | Corrupção da cadeia de evidência (A5) → restore errado (A1, A2); auditoria impossível (viola §2.1 item 7 — motivo/rastreio). |
| Mitigação obrigatória | `operation_id` = 128 bits de CSPRNG (formato hex/ULID), independente de relógio; nome de arquivo do manifesto incorpora o id; escrita de manifesto é create-new: existente ⇒ FALHA FECHADA da operação (nunca overwrite, nunca append em manifesto de outro lote). Índice de operações é append-only. Relógio de parede entra só como campo informativo `timestamp`, nunca como chave. |
| Teste que prova | `Security_OperationIdCollision_PreexistingManifestFailsClosed` — força id duplicado (injeção); afirma: segunda operação falha sem tocar o manifesto existente, primeira permanece íntegra, exit code de erro operacional. Companheiro: `OperationId_Entropy_TwoConcurrentBatches_NeverCollide`. |

### T-10 — Disco cheio no meio do move — severidade P0

| Item | Detalhe |
|---|---|
| Vetor | Move entre volumes degrada para copy+delete. Disco da quarentena enche no meio da cópia → fragmento na quarentena; implementação ingênua já apagou a fonte, ou declara sucesso com destino truncado. Resultado: metade dos bytes em cada lado. Mesmo em rename same-volume, falta de fsync permite sucesso lógico sobre estado não persistido após queda de energia. |
| Exemplo | Lote de 40 GB para HD externo quase cheio; cópia morre em 70%; fonte removida "porque move"; usuário perde 30% dos bytes de cada arquivo do lote. |
| Impacto | Perda parcial silenciosa (A1) — talvez pior que perda total, porque passa despercebida até o restore. |
| Mitigação obrigatória | Protocolo de move seguro, mesmo passo a passo: (1) copiar para `<dest>.<op-partial>` nome temporário; (2) `FlushFileBuffers` (fsync) no temporário; (3) reabrir e re-hashear o temporário (BLAKE3, cobre também T-04); (4) hash bate → rename atômico temporário→final; (5) só ENTÃO liberar a fonte (delete do temporário-fonte no caso cross-volume; no same-volume o rename já é atômico e o passo vira verificação de existência); (6) escrever manifesto + fsync do manifesto; (7) declarar sucesso. Qualquer passo falha → remover temporário, fonte INTACTA, operação FALHA no índice, exit code 1. Pré-checagem de espaço livre estimado antes do lote (rejeita lote impossível antes de tocar em qualquer arquivo). |
| Teste que prova | `Security_DiskFullMidCopy_FailClosed_SourceIntact_NoPartialDeclaredSuccess` — quota/tempfs pequeno ou falha injetada no passo 2; afirma: fonte presente byte-a-byte, temporário removido, índice marca FALHA, exit code 1. Companheiro: `Quarantine_SuccessRequiresFsyncOfDataAndManifest` (assert de chamada de flush antes do registro de sucesso). |

### T-11 — Permissões NTFS negadas — severidade P2

| Item | Detalhe |
|---|---|
| Vetor | ACL `DENY` de leitura em arquivo individual (ou herança quebrada em subárvore); enumeração vê o nome, abertura falha com `ERROR_ACCESS_DENIED`. Também: quarentena em volume/pasta com ACL que nega write ao usuário. |
| Exemplo | Pasta `financeiro` com ACL restritiva dentro da árvore escaneada; scan não consegue hash de 12 arquivos; lote de resolução segue sem eles e o relatório cala — usuário acredita que a pasta toda foi analisada. |
| Impacto | Silenciosamente incompleto = análise mentidosa (A3); pior variante: arquivo sem hash elegível para resolução porque "parecia duplicata pelo nome+tamanho". |
| Mitigação obrigatória | Falha por-item explícita: erro de acesso vira status `ACCESS_DENIED` no relatório (com contadores), nunca aborta o scan inteiro nem é engolido. REGRA DURA: arquivo sem hash BLAKE3 verificado NUNCA é elegível para resolução/quarentena — sem evidência, sem remoção (fail-closed, coerente com prioridades §51). Resumo do relatório exibe "X arquivos não analisados por acesso negado" na primeira tela (§15). Produto jamais exige elevação para completar: o que o usuário não lê, o produto não decide. Exit code 3 (parcial) quando houver itens não analisados. |
| Teste que prova | `Permissions_AccessDenied_FileReportedAndNeverQuarantineEligible` — ACL deny simulada (chmod em POSIX-fs de teste / mock de abstração de FS); afirma: status ACCESS_DENIED no relatório, arquivo fora de candidatos de resolução, exit 3 no modo parcial. Companheiro: `Scan_PartialFailures_DoNotAbortWholeScan` (demais arquivos analisados normalmente). |

## 4. Regras derivadas (obrigatórias para todo código de produto)

Estas regras são exigências vinculantes extraídas dos casos acima. Cards de implementação
devem citá-las; o reviewer cobra cada uma no GATE 5.

```text
R1  (T-06)  Quarentena fora da raiz escaneada OU, se interna, subárvore ConflictDoctor/
            excluída da enumeração por prefixo de bytes, com contador dedicado.

R2  (T-01)  Nenhum caminho manipulado como string crua: canonização única na entrada +
            forma estendida \\?\ + verificação de contenção byte-a-byte antes de toda
            operação de escrita/move. Proibida concatenação ad hoc de caminhos.

R3  (T-02/T-03) Reparse point nunca é descido nem seguido; diretório com reparse é folha;
            duplo gate de atributos (enumeração + re-checagem imediata pré-abertura).

R4  (T-04/T-05) Hash somente via handle com partilha restrita (sem WRITE/DELETE share);
            metadados (size, mtime, file_id) capturados antes e conferidos depois da
            leitura; divergência => UNSTABLE, fora de decisões, fora do cache.

R5  (T-04/T-10) Verificação pós-move por hash (BLAKE3 recalculado no destino) antes de
            declarar sucesso; manifesto registra hash_pre_move e hash_post_move.

R6  (T-10)  fsync (FlushFileBuffers) dos dados E do manifesto antes de qualquer sucesso;
            protocolo de move seguro: temp → flush → rehash → rename atômico → libera
            fonte → manifesto → sucesso. Falha em qualquer passo: fonte intacta, FALHA.

R7  (T-07)  Restore nunca sobrescreve: destino existente com conteúdo diverso vai para
            irmão determinístico "(restaurado <op>)" ou falha pedindo decisão; comparação
            por hash, nunca por mtime/tamanho isolados.

R8  (T-08)  Cache válido somente com (volume_serial, file_id, size, mtime_ticks,
            algorithm, hash_version) todos iguais; divergência => rehash; cache acelera,
            nunca decide sozinho; uso de cache em decisão gera trilha de auditoria.

R9  (T-09)  operation_id de CSPRNG 128 bits; manifesto é create-new; existente => falha
            fechada; índice de operações append-only; timestamp nunca é chave.

R10 (T-05/T-11) Arquivo não hashável (ACL negada, UNSTABLE, indisponível) nunca é elegível
            para resolução/quarentena; falhas por-item aparecem no relatório e no resumo
            da primeira tela; scan parcial => exit code próprio.

R11 (todas) Falha em etapa destrutiva encerra o processo de forma conservadora (ADR-0002
            §4); nada parcial permanece sem registro no índice de operações.

R12 (T-01/S5) Escape estrito de nomes na saída JSON e na GUI; caracteres bidi marcados;
            agrupamento por bytes UTF-8 exatos, sem fold visual/locale.
```

## 5. Testes exigidos (consolidado para GATE 5)

Nomes canônicos em `tests/Doctor.Tests`. Os cards de implementação correspondentes devem
criar exatamente estes testes (ou superconjunto com rastreio a este documento).

| Teste | Prova | Caso | Prioridade |
|---|---|---|---|
| `Security_PathTraversal_HostileName_ContainedInRoot` | Contenção byte-a-byte sob nomes hostis | T-01 | P0 |
| `Security_LongPath_Over260Chars_ExtendedPrefixNoTruncation` | Sem truncamento acima de 260 chars | T-01 | P0 |
| `Report_BidiControlChars_EscapedInJsonAndGui` | Nome RLO não engana saída | T-01 | P2 |
| `Security_JunctionLoop_TerminatesWithoutDescent` | Ciclo termina; reparse é folha | T-02 | P1 |
| `Security_ReparseDir_PointingOutsideRoot_NotEntered` | Nada fora da raiz é lido | T-02 | P1 |
| `Placeholder_GateBeforeOpen_ZeroBytesRead` | `placeholder_bytes_read == 0` (§21) | T-03 | P1 |
| `Placeholder_ConvertedAfterEnumeration_IsCaughtBySecondGate` | Duplo gate funciona | T-03 | P1 |
| `Security_Toctou_ContentSwappedBetweenHashAndMove_PostMoveHashRollsBack` | Rollback por hash pós-move | T-04 | P0 |
| `Quarantine_ShareModeExclusive_BlockWriterDuringHashWindow` | Janela TOCTOU minimizada | T-04 | P0 |
| `Scan_FileModifiedDuringRead_MarkedUnstable_AndNeverCached` | Instável não classifica nem envenena cache | T-05 | P0 |
| `Scan_StableFile_MetadataUnchanged_ClassifiedNormally` | Controle positivo do snapshot | T-05 | P1 |
| `Security_QuarantineInsideScannedRoot_ExcludedFromEnumeration` | Idempotência do scan pós-resolução | T-06 | P1 |
| `Restore_DestinationExists_NeverOverwrites_RestoresAsDeterministicSibling` | Nunca sobrescreve | T-07 | P0 |
| `Restore_DestinationAbsent_MovesBackAndHashMatches` | Caminho feliz do restore | T-07 | P0 |
| `Restore_DestinationIdentical_AlreadyPresentNoMove` | Idempotência do restore | T-07 | P1 |
| `Cache_FileIdReused_SizeMtimeDiffer_EntryInvalidated_Rehashes` | Reuso de ID não envenena | T-08 | P0 |
| `Cache_VolumeSerialDiffers_SameFileId_Miss` | IDs por volume não colidem | T-08 | P1 |
| `Security_OperationIdCollision_PreexistingManifestFailsClosed` | Manifesto nunca sobrescrito | T-09 | P0 |
| `OperationId_Entropy_TwoConcurrentBatches_NeverCollide` | Entropia do id | T-09 | P1 |
| `Security_DiskFullMidCopy_FailClosed_SourceIntact_NoPartialDeclaredSuccess` | Falha fechada sem perda | T-10 | P0 |
| `Quarantine_SuccessRequiresFsyncOfDataAndManifest` | Sucesso só após flush | T-10 | P0 |
| `Permissions_AccessDenied_FileReportedAndNeverQuarantineEligible` | Sem hash, sem resolução | T-11 | P1 |
| `Scan_PartialFailures_DoNotAbortWholeScan` | Parcial é explícito, não silencioso | T-11 | P1 |

Critérios de pronto desta seção: os testes P0 bloqueiam GATE 3 (Resolution Safety) e GATE 5
(Security); os P1 bloqueiam GATE 5; os P2 entram antes do RC.

## 6. Notas por superfície restante (CLI, GUI, atualizador)

- **S5 CLI:** sem invocação de shell; argumentos parseados, nunca interpretados; JSON com escape
  estrito (R12) e `report_schema_version` sempre na primeira linha lógica; exit codes estáveis
  conforme design do CLI (0/1/2/3 da SPEC §14) — automação RMM nunca deve inferir sucesso de
  um scan parcial. Cobertura: testes de contrato de exit code pertencem ao EPIC 09; este
  documento fixa a exigência de que exit 3 seja emitido em qualquer scan com itens não analisados.
- **S6 GUI:** nenhuma lógica de motor (ADR-0003); ações destrutivas visualmente inequívocas (§37);
  diálogo de confirmação exibe caminho de origem, caminho de quarentena e hash — o usuário aprova
  evidência, não um resumo. Nomes hostis escapados (R12). A GUI nunca oferece "apagar" — só
  "mover para quarentena" (ADR-0002 consequências).
- **S7 Atualizador (futuro):** ameaça de supply chain — update troca o binário e compromete tudo
  que este modelo garantiu. Exigências mínimas quando existir: manifesto assinado Ed25519 com
  chave pública embutida, hash do binário verificado antes de execução, sem auto-execução
  silenciosa; canal Microsoft Store já entrega distribuição assinada. Decisão detalhada pertence
  ao ADR de updater (EPIC 14); este documento fixa o piso de segurança.

## 7. Riscos residuais assumidos

1. Malware rodando como o mesmo usuário pode fazer qualquer coisa aos arquivos — o produto não
   defende contra adversário com privilégios do usuário; defende contra si mesmo falhando.
2. Queda de energia entre rename atômico e fsync do manifesto pode deixar arquivo em quarentena
   sem manifesto correspondente; mitigação: rotina de reconciliação (varredura de órfãos na
   inicialização, entradas órfãs ficam em quarentena, nunca restauradas automaticamente) — card
   futuro do EPIC 08.
3. Colisão de BLAKE3 é desprezada (força bruta impraticável); a defesa real contra hash errado é
   o snapshot de metadados (R4) e o hash pós-move (R5), que cobrem as causas plausíveis.
