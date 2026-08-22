# Estratégia de testes — Cloud Sync Conflict Doctor (T05)

| | |
|---|---|
| **Card** | t_4958fec7 — T05 Test strategy (papel: qa) |
| **Data** | 2026-08-22 |
| **Revisão** | 1 |
| **Responsável** | André Santo (forg3) \<andre@junkyardgoodies.app\> |
| **Escopo** | Documento de estratégia. Nenhum código de produto criado neste card. |

Fontes lidas na íntegra nesta sessão antes de qualquer citação: `docs/SPEC.md`
(1822 linhas), `docs/adr/ADR-0001.md`, `ADR-0002.md`, `ADR-0003.md`,
`README.md` e `docs/reconhecimento.md` (branch `wt/t_76be6462`). Os contratos de
interface citados (`IFileEnumerator`, `IHasher` etc.) são os definidos pelo card
irmão T01 em `docs/contratos.md`; onde este documento depende de forma exata de
assinatura, está marcado como condição de alinhamento na seção 9.

Convenção de leitura: cada teste obrigatório tem **ID estável**, **nome proposto
de método xUnit**, **nível** (unit / integração / native-windows / bench),
**referência de SPEC** e **critério de aprovação** verificável. Suíte verde =
todos os testes da família passando no(s) sistema(s) operacional(is) exigidos.

---

## 1. Pirâmide de testes

De baixo para cima. O volume diminui, o custo e a fidelidade aumentam.

```text
        ┌─────────────────────────────────────────────┐
        │ BENCHMARK (§23–24)                          │  smoke 500 no CI noturno;
        │ datasets gerados, métricas, regressão       │  1M arquivos fora do PR
        ├─────────────────────────────────────────────┤
        │ DETERMINISMO (§20, ADR-0003)                │  3+ scans, ordens injetadas,
        │ byte-a-byte, roda nos DOIS SO no CI         │  saída comparada byte a byte
        ├─────────────────────────────────────────────┤
        │ SEGURANÇA (§22, ADR-0002, threat model T02) │  não-destruição, quarentena,
        │                                             │  restore, guarda estática
        ├─────────────────────────────────────────────┤
        │ INTEGRAÇÃO                                  │  árvores sintéticas reais em
        │ (filesystem tmp)                            │  diretório temporário, pipeline
        │                                             │  inteiro, CLI de ponta a ponta
        ├─────────────────────────────────────────────┤
        │ UNIT (xUnit)                                │  comparador de bytes, tie-break,
        │                                             │  normalização, schema, VMs GUI
        └─────────────────────────────────────────────┘
```

Regras que governam todas as camadas:

1. **TDD (§19, §46).** Feature crítica começa com teste vermelho. Nenhuma
   classe de `Doctor.Core` entra na main sem teste que a exercite.
2. **Determinismo é testado, não declarado (§3, §20).** Todo teste de saída
   compara bytes, nunca "mesmo conteúdo lógico".
3. **Prioridade (§51)** correctness > safety > determinism > data preservation >
   performance > UX orienta qual suíte bloqueia qual gate (seção 8).
4. **Anti-overengineering (§52).** Nada de framework de teste adicional além de
   xUnit + coverlet; nada de mock library pesada — os falsos (fake/spy) são
   escritos à mão contra os contratos do T01, porque são também especificação
   executável desses contratos.

### 1.1 Categorias xUnit (traits)

| Trait | Valor | Uso |
|---|---|---|
| `Category` | `Determinism`, `Placeholder`, `Safety`, `Cache`, `Report`, `Grouping`, `Hashing`, `Cli`, `GuiVm`, `Security`, `Benchmark` | seleção por família e por gate |
| `OS` | `Windows` | testes nativos Windows; excluídos do job Linux via filtro |

Filtros canônicos:

```bash
# job Linux (e qualquer dev em Unix):
dotnet test --filter "Category!=WindowsNative"
# equivalente: tudo com Trait "OS=Windows" fica fora

# job Windows:
dotnet test  # completo, incluindo native-windows
```

---

## 2. Costuras de teste (seams) — decisões de projeto

O produto só é testável nos pontos abaixo se os contratos do T01 reservarem
estas costuras. São requisitos da estratégia de testes aos contratos:

### 2.1 Injeção de ordem — `IFileEnumerator`

Contrato: `IFileEnumerator.Enumerate(root)` produz entradas Level 0 em **ordem
não especificada**. O pipeline é obrigado a ordenar internamente (bytes UTF-8 do
caminho) antes de qualquer agrupamento, decisão ou saída. Assim a ordem de
enumeração pode ser injetada sem tocar no pipeline:

```csharp
// Implementações de teste sobre o MESMO contrato de produção:
sealed class ReversedFileEnumerator(IFileEnumerator inner) : IFileEnumerator
    => Enumerate() = inner.Enumera().Reverse();            // ordem #2

sealed class ShuffledFileEnumerator(IFileEnumerator inner, int seed) : IFileEnumerator
    // Fisher-Yates com Random compartilhável de seed FIXA declarada no teste.
    // Nunca Random sem seed: o teste tem de ser reproduzível na falha.
```

Scan A = enumerador real (ordem do filesystem); scan B = reverso; scan C =
embaralhado (seed fixa). Saídas comparadas byte a byte (§20).

### 2.2 Prova de não-abertura — `IHasher` espião e fábrica de streams

```csharp
sealed class CountingHasher : IHasher
{
    List<string> PartialCalls, FullCalls;   // registra TODO path recebido
    int OpenedStreams;                      // fábrica de streams instrumentada
}
```

Um placeholder provado intocado exige DUAS evidências independentes (§21):

1. nenhuma chamada de hash cujo path seja placeholder;
2. nenhum `Stream` aberto sobre placeholder (contador de aberturas) e
   `placeholder_bytes_read == 0` na telemetria do relatório.

### 2.3 Tempo congelado — `TimeProvider`

Comparação byte-a-byte entre scans só é possível se `generated_from`
(`scan_started_utc`, `scan_finished_utc`) for idêntico. O relator recebe
`TimeProvider` (padrão .NET 8); os testes injetam `FakeTimeProvider` congelado.
Sem essa costura, §20 é impossível de satisfazer literalmente — portanto é
**condição ao T03**: o único tempo presente no relatório vem do `TimeProvider`
injetado.

### 2.4 Caminhos relativos à raiz escaneada

Para o golden file rodar igual no Ubuntu e no Windows (teste DET-06, pergunta
Q12 do §58), o relatório serializa caminhos **relativos à raiz escaneada**, com
separador `/` normalizado. **Condição ao T03** (schema v1): sem isso, o mesmo
dataset produz bytes diferentes entre máquinas por causa do prefixo absoluto.

### 2.5 Quarentena com raiz injetável — `IQuarantine`

A raiz da quarentena (`<raiz>/ConflictDoctor/quarantine/<timestamp>/`) é
recebida por parâmetro/injeção, nunca resolvida globalmente. Permite testar em
diretório temporário e permite o vetor "quarentena dentro da árvore" do T02 ser
testado de verdade (exclusão da enumeração).

---

## 3. Mapa SPEC → testes obrigatórios

IDs são estáveis e citáveis por outros cards (threat model T02 referencia o
namespace `SEG-*`; este documento não inventa nomes dentro dele — seção 3.9).

### 3.1 Determinismo — família DET (§20; ADR-0003; GATE 2)

| ID | Nome proposto (xUnit) | Nível | Critério de aprovação |
|---|---|---|---|
| DET-01 | `Scan_ThreeEnumerationOrders_ReportsByteIdentical` | integração | Árvore FX-MIN; scans com enumerador real, reverso e embaralhado (seed 42), `TimeProvider` congelado. Os 3 JSON têm bytes idênticos (comparação byte a byte). Roda nos dois SO. |
| DET-02 | `Scan_TenFixedShuffleSeeds_AllReportsByteIdentical` | integração | 10 seeds fixas declaradas no código do teste (sem aleatoriedade no teste). Todas as saídas idênticas às de DET-01. |
| DET-03 | `ParallelHashing_ThreadCompletionOrder_ReportBytesIdentical` | integração | Pipeline com pool de hash paralelo ativo + enumerador embaralhado; saída idêntica à do scan serial de DET-01. Prova que decisão não depende de ordem de threads (§11, §20). |
| DET-04 | `PathOrdering_UsesUtf8ByteOrder_RegardlessOfCulture` | unit | `PathByteComparer` sobre nomes-armadilha (`Zebra.txt`, `apple.txt`, `Apple.txt`, `Árvore.txt`, `zebra.txt`) com `CurrentCulture` forçada a `tr-TR` e depois `pt-BR`. Ordem resultante == ordem de bytes UTF-8 esperada, explícita no teste, em ambas as culturas. |
| DET-05 | `TieBreak_MtimeThenSizeThenPath_NeverFirstSeen` | unit | Resolvedor de regra (KEEP_NEWEST e demais, §17) com empates construídos: mtime igual → decide size; size igual → decide path bytes; entrada apresentada em ordens invertidas produz sempre o mesmo vencedor. Nunca "first seen". |
| DET-06 | `GoldenReport_FixtureMin_MatchesCommittedGoldenBytes` | integração | FX-GOLDEN (árvore fixa versionada) → relatório comparado a golden file commitado. Passa no job Ubuntu E no Windows (depende das condições 2.3 e 2.4). Fecha a pergunta Q12 do §58 no que é automatizável. |

### 3.2 Placeholder — família PLH (§6, §21; GATE 2)

| ID | Nome proposto (xUnit) | Nível | Critério de aprovação |
|---|---|---|---|
| PLH-01 | `PlaceholderEntries_NeverHashed_NeverOpened_ZeroBytesRead` | unit | `FakeFileEnumerator` devolve mistura de normais e placeholders com atributos OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS / reparse. Com `CountingHasher`: zero chamadas de hash em path placeholder, zero streams abertos sobre placeholder, telemetria `placeholder_bytes_read == 0`, `files_placeholder` correto. |
| PLH-02 | `MixedTree_NormalAndSidecarPlaceholders_PipelineCompletesZeroPlaceholderBytes` | integração | Árvore FX-PLACEHOLDER em tmp (Linux: placeholders simulados via sidecar `.placeholder-meta.json`, convenção T04). Scan completa; `placeholder_bytes_read == 0`; conteúdo dos placeholders intacto byte a byte. |
| PLH-03 | `WindowsNative_RealOfflineRecallAndReparseAttributes_ZeroBytesRead` | native-windows | Em Windows real: `FILE_ATTRIBUTE_OFFLINE` setado via P/Invoke, junction, symlink, ROO/RODA quando o FS permitir. Mesmos critérios de PLH-01 sobre o enumerador de produção (FindFirstFileEx). Este teste é a razão de existir do job Windows. |
| PLH-04 | `ReparseLoop_JunctionOrSymlinkCycle_TerminatesWithoutTraversing` | integração (+variante native-windows) | Ciclo de junction (Win) / symlink (Linux) dentro da árvore. Enumeração termina em tempo finito, não atravessa o ciclo duas vezes, nada do ciclo é lido (`bytes_read` do ciclo == 0). Vetor do threat model T02. |

### 3.3 Não-destruição e quarentena — famílias NDES/QRT (§22; ADR-0002; GATE 3)

| ID | Nome proposto (xUnit) | Nível | Critério de aprovação |
|---|---|---|---|
| NDES-01 | `Resolution_KeepNewest_OriginalMoved_QuarantineAndManifestExist_HashMatches` | integração | Após resolução: arquivo sai do caminho original, existe na quarentena datada, manifesto JSON existe com todos os campos do ADR-0002, hash do manifesto == BLAKE3 recalculado do arquivo movido, mtime/size preservados. |
| NDES-02 | `Restore_AfterQuarantine_FileBackByteIdentical_HashVerified` | integração | Restore recoloca o arquivo no caminho original; conteúdo byte-idêntico ao pré-move; hash confere; registro de restore no histórico da operação. |
| NDES-03 | `Restore_TargetPathOccupied_NeverOverwrites_ConservativeFailure` | integração | Arquivo novo ocupa o caminho original. Restore NÃO sobrescreve (ADR-0002 item 3): falha conservadora ou desvio registrado, ocupante intacto, quarentena intacta. |
| NDES-04 | `MoveToFails_MidOperation_NoUnrecordedPartialState` | integração | Movimentador injetado falha após iniciar (simula disco cheio/permissão). Estado consistente: ou move completo com manifesto, ou nada movido; processo encerra de forma conservadora; nada parcial sem registro (ADR-0002 item 4). |
| NDES-05 | `SourceGuard_NoDeleteApisInProductCode` | unit (guarda estática) | Varre texto de `src/**/*.cs`: proibido `File.Delete`, `Directory.Delete`, `DeleteFile`/`DeleteFileW` (P/Invoke), `FILE_DISPOSITION_INFO`/`SetFileInformationByHandle(FileDispositionInfo*)`, `FILE_RENAME_INFO*` fora do mover de quarentena. Allowlist vazia na v1. Zero ocorrências = passa. Complementa a revisão estática do reviewer (ADR-0002 item 5). |
| QRT-06 | `Manifest_CompleteDeterministicFields_NoIncidentalTimeInLists` | unit | Manifestos de operações repetidas sobre a mesma árvore têm entradas idênticas exceto `operation_id`/timestamp de cabeçalho; campos exatamente os do ADR-0002; ordenação por caminho em bytes. |

### 3.4 Cache incremental — família CACHE (§12)

| ID | Nome proposto (xUnit) | Nível | Critério de aprovação |
|---|---|---|---|
| CACHE-01 | `RenameFile_Rescan_CacheReused_NoFullHashRerun` | integração | Renomeia arquivo (mesmo file ID/inode), rescan com `CountingHasher`: zero hash completo novo para ele; grupo/classificação idênticos. Chave lógica por file ID, não por caminho. |
| CACHE-02 | `ContentChanged_SameSize_NewMtime_CacheInvalidated` | integração | Conteúdo trocado mantendo tamanho, mtime avança: rescan recalcula (partial/full conforme pipeline) e classifica corretamente como divergência. |
| CACHE-03 | `CacheEntry_AlgorithmOrHashVersionMismatch_Recalculated` | unit/integração | Entrada de cache com `algorithm`/`hash_version` distintos dos vigentes é ignorada e recalculada; nunca reaproveitada. |
| CACHE-04 | `PoisonedCache_ReusedFileId_SizeOrMtimeMismatch_Recalculated` | integração | Vetor do T02: linha de cache venenosa (file ID reusado com size/mtime divergentes do disco). Cache não pode mascarar conteúdo real: recálculo obrigatório quando metadados não conferem. |

### 3.5 Relatório — família RPT (§13; ADR-0003; schema do T03)

| ID | Nome proposto (xUnit) | Nível | Critério de aprovação |
|---|---|---|---|
| RPT-01 | `Report_ConformsToSchemaV1_TypesRequiredFieldsPresent` | unit | Relatório de FX-MIN valida campo a campo contra `docs/schema-report-v1.md`; fixture `fixtures/report-v1-exemplo.json` do T03 usada como referência de forma. |
| RPT-02 | `Report_ListsSortedByPathBytes_NoIncidentalTimestampInsideLists` | unit | Toda lista do JSON em ordem de bytes UTF-8 de caminho; nenhum campo de tempo dentro de itens de lista (regra 3 do ADR-0003). |
| RPT-03 | `Report_Blake3Explicit_AndVersionsPresent` | unit | `algorithm == "BLAKE3"`, `hash_version == 1`, `report_schema_version == 1` presentes (§4). Troca futura de algoritmo sem bump = teste continua pegando. |

### 3.6 Agrupamento e hashing — famílias GRP/HSH (§7–§9)

| ID | Nome proposto (xUnit) | Nível | Critério de aprovação |
|---|---|---|---|
| GRP-01 | `Normalization_KnownSuffixList_BaseNameExtracted` | unit | Lista fixa e versionada de padrões (§7): ` (1)`, ` (2)`, ` (conflicted copy)`, `-DESKTOP-XXXX`, `~`, `~$`, `.sb-<hex>`. Cada padrão com caso positivo e negativo (nome sem sufixo não é alterado). |
| GRP-02 | `Normalization_UnknownSuffix_LeftAlone_LimitedNotInfinite` | unit | Sufixo fora da lista não normaliza (sem heurística infinita); versão da normalização registrada no relatório. |
| GRP-03 | `Grouping_NormalizedNamePlusSize_OnlyEqualSizesGrouped` | unit/integração | Candidatos agrupados por base normalizada + size; tamanhos diferentes nunca agrupam. |
| HSH-01 | `PartialHash_ReadsOnlyFirst64KiBAndLast64KiB` | unit | Stream espião sobre arquivo de 1 MiB: bytes lidos ≤ 128 KiB, padrão início+fim (§8). |
| HSH-02 | `FullHash_ExecutedOnlyForPartialSurvivors` | integração | Árvore com pares mesmo-tamanho/conteúdo-distinto: `CountingHasher` prova que quem morreu no Level 2 nunca teve hash completo (§8, §9, §10). |
| HSH-03 | `IdenticalContent_FullHashEqual_ClassifiedIdenticalDuplicate` | integração | Cópias byte-idênticas → `identical_duplicates`. |
| HSH-04 | `DifferentContent_SameNormalizedBase_ClassifiedRealConflict` | integração | Mesmo nome normalizado, conteúdo diferente → `real_conflicts`; conflito real jamais aparece como duplicata (Q9/Q10 do §58). |

### 3.7 CLI — família CLI (§14)

| ID | Nome proposto (xUnit) | Nível | Critério de aprovação |
|---|---|---|---|
| CLI-01 | `Scan_CleanTree_Exit0_QuietNoStdout` | integração | Árvore sem anomalias → exit 0; `--quiet` sem saída em stdout. |
| CLI-02 | `Scan_TreeWithConflicts_Exit2_JsonValidAgainstSchema` | integração | Árvore com duplicatas/conflitos → exit 2; `--json` emite relatório válido (RPT-01). |
| CLI-03 | `Scan_OperationalError_Exit1` | integração | Raiz inexistente/sem permissão → exit 1, mensagem em stderr. |
| CLI-04 | `Scan_PartialCompletion_Exit3` | integração | Falha parcial injetada durante scan (subárvore ilegível) → exit 3 e relatório marca incompletude. |

Exit codes formais 0/1/2/3 pertencem ao ADR-0008 (T01); esta suíte os trava.

### 3.8 GUI (ViewModel) e benchmark — famílias GUIVM/BENCH (§15, §23–24)

| ID | Nome proposto (xUnit) | Nível | Critério de aprovação |
|---|---|---|---|
| GUIVM-01 | `SummaryViewModel_AnswersFiveQuestions_FromReportData` | unit | As 5 perguntas do §15 respondidas a partir do modelo de domínio/JSON (dados fake DEMO do T06 enquanto o motor real não existe). GUI sem segundo motor: VM consome o schema. |
| GUIVM-02 | `DestructiveActionLabels_AlwaysQuarantine_NeverDelete` | unit (guarda de strings) | Nenhuma string de ação da GUI rotula destruição como "apagar"; rótulo canônico "Mover para quarentena". Guarda textual sobre recursos/strings da GUI. |
| BENCH-01 | `DatasetGenerator_SameSeed_TwoRunsIdenticalListing` | bench-smoke | `scripts/bench/generate_dataset.py` (T04) rodado 2× com mesmo seed: `find \| sort \| md5sum` idêntico. Pré-condição de confiança em todo benchmark. |
| BENCH-02 | `SmokeDataset500_CountersMatchParameters` | bench-smoke | Dataset fumaca de 500: contagens de únicos/duplicatas/conflitos/placeholders conferem com os parâmetros passados. |
| BENCH-03 | `FullScale1M_MetricsArtifactRecorded` | bench (fora do PR) | Meta §23 (1M arquivos): roda em janela agendada/manual conforme procedimento do T04 (`docs/benchmark-harness.md`); publica `metrics.json` (wall_clock, files_enumerated/read, bytes_read partial/full, peak RSS via `/usr/bin/time -v`, CPU). Critério de regressão: aumento de `files_full_hashed`/`bytes_read_full` ou wall_clock > limite definido pelo T04 exige investigação antes de merge. Nenhum número aqui é inventado: vale o que o T04 medir. |

### 3.9 Segurança — namespace SEG (cross-ref T02)

Reservado ao `docs/threat-model.md` (card T02). Regra de integração: **todo caso
concreto do threat model aponta exatamente um `SEG-nn` com nome de teste,
nível e critério**; casos automatizáveis entram em `tests/Doctor.Tests`
(categoria `Security`), casos manuais entram na checklist da auditoria final
(seção 4). Se um caso do T02 coincidir com teste já mapeado aqui (ex.:
PLH-04, CACHE-04, NDES-03/04), o T02 referencia o ID existente em vez de
duplicar. Colisão de nome resolve-se no T02; IDs desta tabela não mudam.

---

## 4. §58 — Auditoria final: perguntas virando testes

Cada pergunta do "CAN I TRUST THE DELETE BUTTON?" com sua cobertura
executável. Status: AUTOMATIZADO (teste já mapeado acima) / PARCIAL (automatizado +
procedimento manual na auditoria) .

| # | Pergunta (§58) | Cobertura | Status |
|---|---|---|---|
| Q1 | Pode apagar o arquivo errado? | NDES-01..05; DET-05 (decisão de vencedor correta); revisão manual do allowlist da guarda estática | PARCIAL |
| Q2 | Pode ler um placeholder? | PLH-01..04 (dupla evidência: hash + abertura de stream) | AUTOMATIZADO |
| Q3 | Resultados diferentes entre scans? | DET-01, DET-02, DET-06 | AUTOMATIZADO |
| Q4 | Depender da ordem de threads? | DET-03 (pool paralelo + ordem embaralhada) | AUTOMATIZADO |
| Q5 | Perder arquivo durante quarantine? | NDES-01 (manifesto + hash), NDES-04 (falha no meio), QRT-06 (manifesto completo); procedimento manual: reconciliar manifesto × quarentena × origem em dataset grande | PARCIAL |
| Q6 | Restaurar para o lugar errado? | NDES-02 (caminho exato + hash), NDES-03 (nunca sobrescreve) | AUTOMATIZADO |
| Q7 | Corromper um arquivo? | NDES-01/02 (hash pré/pós move e restore), HSH-01 (limites de leitura) | AUTOMATIZADO |
| Q8 | Relatório inconsistente? | RPT-01..03; CLI-02 | AUTOMATIZADO |
| Q9 | Esconder um conflito real? | HSH-04; GRP-02 (normalização não engole divergência) | AUTOMATIZADO |
| Q10 | Classificar diferentes como idênticas? | HSH-02 (pipeline L2/L3), HSH-03 contra pares deliberadamente distintos; propriedade: conteúdos conhecidos-diferentes nunca classificam idênticos | AUTOMATIZADO |
| Q11 | Perder cache incorretamente? | CACHE-01..04 | AUTOMATIZADO |
| Q12 | Comportamento diferente entre máquinas? | DET-06 (golden file nos dois SO); BENCH-01 (generator reproduzível) | PARCIAL |

A auditoria final (EPIC 19) executa esta tabela como checklist: item
AUTOMATIZADO = suíte verde no release candidate; item PARCIAL = teste verde +
evidência manual registrada no card da auditoria. Qualquer SIM em uma pergunta
do §58 = produto não pronto (§58).

---

## 5. Fixtures — árvores sintéticas padrão

Duas fontes complementares, sem sobreposição:

1. **Builder in-process** (`tests/Doctor.Tests/TestSupport/SyntheticTreeBuilder.cs`)
   — árvores pequenas/médias criadas em `Path.GetTempPath()` pelo próprio teste.
   Determinístico: conteúdos literais fixos, mtimes explícitos
   (`DateTimeOffset` fixo por índice, UTC), seeds fixos. Sem depender de
   subprocesso — rápido o bastante para rodar em toda suíte.
2. **Gerador do T04** (`scripts/bench/generate_dataset.py`, stdlib puro, flags
   `--files --dup-groups --conflict-names --placeholders --depth --seed`) —
   datasets grandes e o dataset de benchmark; usado pelos testes BENCH-* e pela
   bateria noturna. Contrato com T04: mesmo seed ⇒ mesma árvore (provado por
   `find | sort | md5sum` duas vezes); placeholders simulados via sidecar
   `.placeholder-meta.json` com hook documentado para os atributos reais do
   Windows.

Layout e naming padrão (usados por ambos):

```text
<root>/
  unique/                  arquivos sem relação
  dups/                    grupos de cópias idênticas exatas
  conflicts/               mesmo base-name normalizado, conteúdos distintos
  conflict-names/          sufixos reais: "relatorio (1).txt",
                           "foto (conflicted copy).jpg", "-DESKTOP-ABC123",
                           "~planilha.xlsx", "~$doc.docx", ".sb-1a2b3c.pdf"
  deep/l0/l1/.../lN/       profundidade parametrizada
  placeholders/            normais + OFFLINE/ROO/RODA/reparse (por SO)
                           + sidecar .placeholder-meta.json (convenção T04)
```

| Fixture ID | Origem | Conteúdo mínimo | Usada por |
|---|---|---|---|
| FX-MIN | builder | ~12 arquivos: 1 par idêntico, 1 par conflito, 1 único, 1 placeholder, 1 nome-armadilha unicode | DET-01/02/03, RPT-*, GRP-*, HSH-*, CLI-* |
| FX-UNICODE | builder | nomes que diferenciam ordem byte × locale (maiúsculas/minúsculas, acentuadas, turco-I) | DET-04 |
| FX-TIE | builder | pares com mtime/size idênticos construídos | DET-05 |
| FX-PLACEHOLDER | builder + sidecar | mistura normal/OFFLINE/ROO/RODA/reparse | PLH-01/02 |
| FX-LOOP | builder | junction (Win) / symlink (Unix) cíclico | PLH-04 |
| FX-GOLDEN | builder, valores commitados | árvore fixa + golden report commitado em `tests/Doctor.Tests/Fixtures/golden/` | DET-06, Q12 |
| FX-SMOKE500 / FX-1M | gerador T04 | 500 arquivos / 1M arquivos, seed fixo documentado | BENCH-01..03, carga noturna |

Regras: nenhuma fixture carrega dado sensível; mtimes sempre explícitos (nunca
`now()`); nomes ASCII nos fixtures genéricos, unicode isolado na FX-UNICODE
para localizar falhas de ordenação; quarentena de teste SEMPRE fora da raiz
escaneada, exceto nos vetores dedicados do T02.

---

## 6. CI — plano GitHub Actions

Arquivo único `.github/workflows/ci.yml`. Gatilhos: `push` em `main`,
`pull_request`, `schedule` (cron noturno para BENCH-01/02). O SDK .NET 8 vem do
setup-action oficial; cache NuGet por hash de lockfile.

```yaml
name: ci
on:
  push: { branches: [main] }
  pull_request:
  schedule: [{ cron: "0 5 * * *" }]   # noturno: bench smoke

jobs:
  linux:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 8.0.x }
      - run: dotnet build CloudSyncConflictDoctor.sln -c Release
      - run: dotnet test tests/Doctor.Tests -c Release --no-build
             --filter "Category!=WindowsNative"
             --collect:"XPlat Code Coverage"
             --logger "trx;LogFileName=linux.trx"
      - run: dotnet test tests/Doctor.Tests -c Release --no-build
             --filter "Category=Determinism"     # determinismo EXPLICITO no Linux tb
      - uses: actions/upload-artifact@v4
        with: { name: linux-results, path: | tests/**/TestResults/**, coverage }

  windows:
    runs-on: windows-latest      # job onde roda o nativo
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 8.0.x }
      - run: dotnet build CloudSyncConflictDoctor.sln -c Release
      - run: dotnet test tests/Doctor.Tests -c Release --no-build
             --collect:"XPlat Code Coverage"
             --logger "trx;LogFileName=windows.trx"   # SUÍTE COMPLETA
      - uses: actions/upload-artifact@v4
        with: { name: windows-results, path: tests/**/TestResults/** }
```

Decisões do plano:

1. **Dois jobs, sem matriz:** `ubuntu-latest` e `windows-latest`. O job Windows
   é onde vivem os testes `native-windows` (atributos OFFLINE/ROO/RODA reais,
   junctions, FindFirstFileEx, seek penalty); o Linux roda todo o restante,
   incluindo determinismo — §20 exige o teste de determinismo no CI, e roda-lo
   nos dois SO é o que dá força ao golden file (DET-06) contra diferenças de
   plataforma.
2. **Determinismo é checagem separada** (`Category=Determinism`) no Linux para
   aparecer como linha própria no resumo do PR; no Windows faz parte da suíte
   completa.
3. **Artefatos:** TRX de cada job, cobertura (coverlet XML + relatório), e
   `determinism-hashes.txt` (SHA-256 dos relatórios dos scans A/B/C) publicado
   por execução — evidência auditável de determinismo por build. Retenção 30 dias.
4. **Checks obrigatórios de branch protection:** `linux / test` e
   `windows / test`. PR sem os dois verdes não entra.
5. **Benchmark:** apenas BENCH-01/02 no cron noturno (barato). BENCH-03 (1M)
   é disparo manual/agendado longo, fora do PR, com artefato `metrics.json`
   conforme `docs/benchmark-harness.md` do T04.
6. Cobertura publicada como artefato e comentada no PR (resumo por módulo);
   thresholds da seção 8 falham o build, não são advisory.

---

## 7. Cobertura mínima por módulo

Medição: coverlet.collector, contagem de linhas, gate por threshold no CI.
Exclusões: `Program.cs`, code-behind/views da GUI, código gerado.

| Módulo | Linha mínima | Observações |
|---|---|---|
| `src/Doctor.Core` | **85 %** | Classes críticas com meta 100 % de linha: `PathByteComparer`, resolvedor de tie-break, gate de placeholder, mover de quarentena, escritor de relatório |
| `src/Doctor.Cli` | 75 % | Fluxos de exit code cobertos por CLI-01..04 |
| `src/Doctor.Gui` (ViewModels) | 60 % | Só ViewModels contam; views ficam fora da medição |
| `src/Doctor.Gui` (views) | — | Excluída; UX validada por GUIVM-* e inspeção |
| `tests/Doctor.Tests` | — | É o instrumento, não o alvo |

Cobertura é piso, não objetivo: um módulo com 100 % de linha e DET/PLH/NDES
vermelhos não está pronto. A inversão nunca vale.

---

## 8. Gates — quais suítes precisam estar verdes (§45)

| Gate | Suítes verdes exigidas (categoria) | SO |
|---|---|---|
| **GATE 1 — Architecture Ready** | Este documento aprovado é item do gate; nenhuma suíte ainda (esqueleto inexistente) — condição: contratos do T01 contemplarem as costuras da seção 2 | — |
| **GATE 2 — Scanner Correctness** | `Determinism` (DET-01..06), `Placeholder` (PLH-01..04), `Grouping`, `Hashing` (HSH-01..04), `Cache` (CACHE-01..04), `Report` — e cobertura de `Doctor.Core` ≥ 85 % | **ambos** |
| **GATE 3 — Resolution Safety** | `Safety` (NDES-01..05, QRT-06) + casos `SEG-*` automatizados do T02 ligados a quarentena/restore/TOCTOU | ambos (nativo no Windows) |
| **GATE 4 — Performance** | BENCH-01/02 verdes no CI; BENCH-03 executado com `metrics.json` arquivado; sem regressão pelos critérios do T04 | Linux (1M pode ser em host dedicado) |
| **GATE 5 — Security** | Todos os `SEG-*` automatizados do threat model + PLH-04 + CACHE-04 + revisão do installer (quando existir) | ambos |
| **GATE 6 — Release** | TODAS as categorias verdes nos dois jobs; checks obrigatórios do CI passando; golden determinismo (DET-06) verde nos dois SO; CLI-01..04 verdes | ambos |

Regra de travamento: gate anterior fechado é pré-condição do seguinte;
suíte vermelha em categoria de gate aberto congela o downstream correspondente
(não se libera GUI sobre GATE 2 vermelho, não se assina installer sobre GATE 3
vermelho).

---

## 9. Condições a outros cards e riscos

| Condição | Para | Efeito se não atendida |
|---|---|---|
| Costuras da seção 2 (`IFileEnumerator` sem ordem garantida, `IHasher` espiável, `TimeProvider` injetável, caminhos relativos no relatório, raiz de quarentena injetável) | T01 (`docs/contratos.md`), T03 (schema) | Testes byte-a-byte (§20) e prova de não-abertura (§21) ficam impossíveis; os contratos teriam de mudar depois do código existir |
| Formato exato de serialização (UTF-8 sem BOM, newline fixo, ordem de chaves declarada) | T03 | DET-01/02/06 sem significado |
| Gerador determinístico com flags documentadas + sidecar de placeholder | T04 | BENCH-* e FX grandes não confiáveis |
| Nomes `SEG-nn` únicos apontando para IDs desta tabela | T02 | Duplicação de cobertura e gaps silenciosos na auditoria final |
| Avalonia confirmado/corrigido (ADR-0009) e esqueleto de VM do T06 | T01, T06 | GUIVM-01/02 ajustam nomes, sem mudar critérios |

Riscos registados:

1. **Placeholders reais só existem no Windows.** Mitigado pela dupla camada
   PLH (falso em qualquer SO + nativo no job Windows); o sidecar do T04 cobre
   Linux, e a limitação fica documentada em ambos os cards.
2. **Golden file frágil a mudanças legítimas de schema.** Regra: bump de
   `report_schema_version` regenera golden NO MESMO PR que muda o schema, com
   justificativa no corpo do PR — golden nunca atualizado "para voltar a ficar
   verde".
3. **Tempo congelado pode esconder bug de formatação de timestamp.**
   Aceito: o formato do timestamp é validado por RPT-01 contra o schema; o
   valor real é responsabilidade do `TimeProvider` de produção, exercitado em
   integração sem congelamento (DET-03 usa pool real).
4. **Job Windows do GitHub Actions é mais lento e limitado** — aceito; é onde a
  SPEC manda testar o nativo, e o custo é pago só no CI, não no loop local.
