# Round 01 — Benchmark 1M de arquivos (GATE 4)

| Campo | Valor |
|---|---|
| Card | t_f3cb006a (T07 — Rodada benchmark 1M + registro GATE 4) |
| Data | 2026-08-23 |
| Revisão | 1 |
| Responsável | André Santo (forg3) |
| Branch / worktree | wt/t_f3cb006a @ .worktrees/t_f3cb006a |
| Commit do código do scanner | 0ca0530 (main — nenhuma alteração de código nesta rodada) |
| Status | Executada — baseline inicial do benchmark (GATE 4) |

## 1. Objetivo e alcance

Primeira rodada do benchmark sobre o dataset de 1.000.000 de arquivos
(docs/benchmark-harness.md §5), estabelecendo o baseline de medição que o GATE 4
(SPEC §45) exige: benchmark executado, métricas registradas, regressões
documentadas. Por ser a primeira rodada, não há BEFORE: os valores aqui são o
**baseline** contra o qual rodadas futuras serão comparadas (harness §6, regra 4).

**Alcance do estágio executado (declaração explícita exigida pela decisão 3 do
card):** o scanner executável disponível é o Doctor.Cli em main (commit 0ca0530),
que roda o pipeline L0 (enumeração sidecar-aware) → L1 (agrupamento) → L2
(hash parcial BLAKE3). O estágio de veredito por hash completo (L3 →
identical_duplicates / real_conflicts) não produz resultados neste build:
`files_full_hashed = 0`, `bytes_read_full = 0`, `identical_duplicates = []`,
`real_conflicts = []`. Os números desta rodada são REAIS e medidos — nada foi
simulado — mas cobrem somente até L2. Rodadas futuras com L3 integrado devem
usar esta tabela como BEFORE.

## 2. Dataset

Gerador `scripts/bench/generate_dataset.py`, `generator_version = 1`
(determinístico por seed), composição em 10 shards conforme harness §5:

```text
raiz:      /tmp/cd-1m (união das subárvores shard_0 .. shard_9)
por shard: --files 100000 --dup-groups 5000 --conflict-names 5000
           --placeholders 6000 --depth 6
seeds:     20260822, 20260823, ..., 20260831  (shard_0 .. shard_9)
geração:   14:33:16 – 14:47:06 UTC (13m50s), sequencial, log em generation.log
```

Conferência do composto (`count_verification.json`, script verify_counts.py):

```text
arquivos encontrados:  1.060.000  (1.000.000 alvo + 60.000 sidecars .placeholder-meta.json)
arquivos esperados:    1.060.000  (soma dos files_created + sidecars_written dos 10 resumos)
divergência:           0          (todos os 10 shards batem exatamente)
bytes em disco:        76.015.949.061 B (~70,8 GiB)
bytes previstos:       75.999.732.750 B (resumo do gerador; diferença = overhead de diretórios)
```

Resumos JSON de geração por shard versionados em `gen_shard_0..9.json`.

## 3. Condições da máquina

Registradas em `conditions.txt` (início da rodada):

```text
host:      vnic, Ubuntu 24.04 (kernel 6.17.0-1018-oracle), aarch64, VM 4 vCPU
memória:   23 GiB total / ~14 GiB disponíveis, sem swap
disco:     ext4, 153 GiB livres antes da geração (193 GiB totais)
inodes:    25.176.611 livres antes da geração
carga:     load average 1,47–3,08 durante a rodada — host compartilhado com
           outras sessões de trabalho Hermes em paralelo (varreduras e builds
           de cards irmãos). Números absolutos herdam esse ruído; comparações
           antes/depois futuras devem repetir condições equivalentes.
/usr/bin/time ausente → peak_RSS e CPU% via wrapper stdlib
resource.getrusage(RUSAGE_CHILDREN) (harness §3), implementado em run_scan.py.
```

## 4. Procedimento

1. Build Release do Doctor.Cli no worktree da rodada (0 warnings novos).
2. Smoke de validação em dataset de 500 arquivos (seed 42): telemetria emitida,
   gate zero, exit 0.
3. Geração e conferência do dataset 1M (seção 2).
4. Execuções sobre `/tmp/cd-1m`: 1 warm-up descartável + 5 medidas
   (`run_scan.py` → `runwarmup`, `run1..run5` + `.meta.json`), todas exit 0.
5. Agregação por mediana de 5 (`aggregate_round01.py`) e verificação de
   determinismo (`compare_determinism.py`, `fingerprint_runs.py`).

Comando por execução: `dotnet conflictdoctor.dll scan /tmp/cd-1m --json`.

## 5. Tabela-modelo (harness §6)

Mediana de 5 execuções. Baseline: não há BEFORE (primeira rodada).

```text
dataset:        generator_version=1 seeds=20260822..20260831
                params=10x(--files 100000 --dup-groups 5000 --conflict-names 5000
                           --placeholders 6000 --depth 6)
before_commit:  n/a — baseline inicial (código em 0ca0530)
after_commit:   n/a — primeira rodada; valores abaixo são o BASELINE

wall_clock_s:            92,171   (execuções: 118,64 / 92,17 / 121,17 / 76,62 / 20,21)
files_enumerated:        1.000.000
files_read:              16.572   (= files_partial_hashed; ver alcance L2 na seção 1)
bytes_read:              1.364.973.038
bytes_read_partial:      1.364.973.038
bytes_read_full:         0        (L3 fora do estágio executado)
full_hash_count:         0        (idem)
peak_RSS_kb:             1.040.328  (~1,02 GiB)
CPU%:                    34,6     (user+sys/wall via getrusage)
placeholder_bytes_read:  0 -> 0   GATE: PASS nas 5 execuções
```

Contadores complementares (mediana, idênticos nas 5):

```text
files_placeholder:       60.000
files_skipped:           0   (emissão atual conta apenas erros de acesso; ver seção 7)
grupos candidatos (L1):  8.286  (todos com 2 membros: 2 x 8.286 = 16.572 aberturas L2)
placeholders listados:   60.000
erros de acesso:         0
exit code:               0 (limpo) nas 6 execuções
```

## 6. Evidência de determinismo

- Os 6 stdout têm tamanho idêntico (12.413.941 B); diferem apenas pelo bloco
  `generated_from` (timestamps vivos, permitidos pelo ADR-0003).
- Removido o bloco, o SHA-256 do conteúdo canônico é IGUAL nas 6 execuções:
  `1019fd09a88ebef958a87eb10ca3749ff4ef4a956e1fba17390d73d52d103a96`
  (`determinism_check.json`).

## 7. Observações, desvios e limitações

1. **Alcance L0–L2**: sem vereditos L3 neste build, `bytes_read` cobre somente
   janelas parciais/inteiros ≤ 128 KiB (receita ADR-0005). A métrica-chave da
   SPEC §24 ("menos hashes completos") fica sem contraprova nesta rodada;
   próxima rodada (com L3 integrado) usa esta tabela como BEFORE.
2. **Bytes evitados (derivado do relatório, não emitido)**: de
   75.999.732.750 B lógicos, foram lidos 1.364.973.038 B → **98,2% dos bytes não
   precisaram ser lidos** (98,34% dos arquivos enumerados nunca abriram:
   923.428 únicos + 60.000 placeholders). Valor dependente do alcance L2.
3. **Desvio de contrato de telemetria**: a invariante documentada em
   Telemetry.cs (`files_enumerated = placeholder + skipped + partial_hashed`)
   não fecha com a emissão atual — `files_skipped = 0` porque a CLI emite só
   erros de acesso nesse contador; os 923.428 arquivos únicos descartados pelo
   agrupamento não entram em contador algum. Pendência registrada para card de
   alinhamento do contrato (T06/T16); não afeta o gate nem as demais métricas.
4. **Variância de wall_clock alta** (20–121 s): aquecimento progressivo do page
   cache entre execuções + carga de sessões irmãs no host. A mediana (92,171 s)
   segue o protocolo do harness §4. Para comparações futuras mais estreitas,
   considerar cold-cache opcional (§4, requer root).
5. **Limitações herdadas do harness**: placeholders simulados em ext4 (stub +
   sidecar, §2.1 — validação definitiva só em NTFS/Windows); host virtualizado
   ARM de 4 vCPU sob carga compartilhada — números absolutos valem só para
   comparação relativa antes/depois no mesmo host, não como garantia de produto.

## 8. Arquivos da rodada (results/bench/round-01/, versionados na branch)

| Arquivo | Conteúdo |
|---|---|
| `runwarmup`, `run1..run5` | stdout JSON íntegro do scanner (12,4 MB cada) |
| `run*.meta.json` | wall/RSS/CPU/exit por execução (wrapper getrusage) |
| `gen_shard_0..9.json` | resumo JSON da geração por shard |
| `generation.log`, `scans.log` | cronologia da geração e das execuções |
| `count_verification.json` | conferência 1.060.000 == 1.060.000 |
| `aggregate_output.txt` | saída bruta da agregação (mediana de 5) |
| `fingerprints.json`, `determinism_check.json` | evidências de determinismo |
| `conditions.txt` | estado da máquina no início |
| `run_scan.py`, `generate_1m.sh`, `run_all_scans.sh`, `verify_counts.py`, `aggregate_round01.py`, `inspect_telemetry.py`, `compare_determinism.py`, `fingerprint_runs.py`, `summary_counts.py` | metodologia reproduzível |

Dataset `/tmp/cd-1m` removido após o registro desta rodada (harness §5 — 76 GB
não permanecem em disco); é regenerável byte a byte pelos comandos da seção 2.
