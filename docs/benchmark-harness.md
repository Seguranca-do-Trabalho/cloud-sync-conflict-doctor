# Benchmark Harness — Cloud Sync Conflict Doctor

| Campo | Valor |
|---|---|
| Card | t_2c4c7be7 (T04 — Benchmark harness design) |
| Data | 2026-08-22 |
| Revisão | 1 |
| Responsável | André Santo (forg3) |
| Status | Ativo — referência para GATE 4 (Performance) |

## 1. Objetivo

Medir o scanner contra os requisitos da SPEC §10, §23 e §24. A métrica principal
**não** é `scan_seconds`: é uma função de tempo + bytes lidos + arquivos
efetivamente abertos + memória. O benchmark existe para provar três coisas após
cada mudança relevante:

1. menos arquivos lidos;
2. menos bytes lidos;
3. menos hashes completos —

sem regressão de tempo, memória e sem nunca ler um placeholder
(`placeholder_bytes_read == 0`, SPEC §6; violação é falha de segurança, não
métrica de performance).

Regra da casa: **nunca otimizar por feeling; sempre benchmark antes e depois**
(SPEC §23).

## 2. Dataset versionado

Ferramenta: `scripts/bench/generate_dataset.py` (python3 puro, stdlib,
`generator_version: 1`). Determinística por seed: mesmo seed produz árvore
idêntica byte a byte (conteúdo, caminhos, tamanhos e mtimes fixos em
2000-01-01T00:00:00Z), verificável por:

```bash
for R in $A $B; do (cd "$R" && find . -type f -printf '%P\n' | LC_ALL=C sort | md5sum; \
  find . -type f -print0 | LC_ALL=C sort -z | xargs -0 md5sum | md5sum); done
```

Parâmetros:

```bash
python3 scripts/bench/generate_dataset.py \
    --root /tmp/cd-dataset --files 500 --dup-groups 25 \
    --conflict-names 40 --placeholders 30 --depth 4 --seed 42
```

Distribuição resultante (partição exata de `--files`):

| Categoria | Fórmula | Exemplo (500/25/40/30) |
|---|---|---|
| Grupos de duplicata idêntica | `--dup-groups`, 2 arquivos cada, mesmo conteúdo BLAKE-idêntico | 25 grupos / 50 arquivos |
| Divergências reais | `--conflict-names`, 2 arquivos cada, nome normaliza igual, conteúdo difere; grupos de numeração par têm o **mesmo tamanho** (pior caso para o filtro de tamanho do pipeline), ímpares tamanhos distintos | 40 grupos / 80 arquivos |
| Placeholders simulados | `--placeholders`, stub de 4 KiB + sidecar `.placeholder-meta.json` | 30 (+30 sidecars) |
| Únicos | restante | 340 |

Sufixos de conflito gerados seguem a lista fixa da SPEC §7: `" (1)"`,
`" (conflicted copy)"`, `-DESKTOP-XXXX`, `~$` (prefixo Office-lock), `~`,
`.sb-<hex>`.

### 2.1 Placeholders em Linux/ext4 — limitação e hook Windows

`FILE_ATTRIBUTE_OFFLINE`, `RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS` e reparse
points **não existem em ext4**. O gerador simula o comportamento:

- stub de 4 KiB cujo conteúdo começa com o marcador legível
  `PLACEHOLDER-SIMULADO` — se o scanner abrir por engano, lê bytes > 0 e a
  violação fica detectável no benchmark;
- sidecar `<arquivo>.placeholder-meta.json` registrando `simulated_attributes`
  (atributo OFFLINE-family simulado), `provider`, `original_size` real simulado
  e nota de limitação.

O sidecar é a fonte da verdade para o harness decidir quais caminhos eram
placeholder no scan. **Hook Windows:** quando o scanner rodar em NTFS, o
benchmark deve validar os atributos reais via enumeração Level 0
(`FILE_ATTRIBUTE_*` + reparse) em vez do sidecar; a lista de caminhos esperados
continua vindo do mesmo dataset. Teste automatizado correspondente: scan do
dataset deve terminar com `placeholder_bytes_read == 0`; um scanner que trata
os stubs como arquivo normal leria `30 × 4 KiB` a mais em `bytes_read`, e isso
constitui falha, não variação aceitável.

### 2.2 Reprodutibilidade

- Toda aleatoriedade vem de `random.Random(seed)`; nenhum uso de tempo,
  hash randomizado ou ordem de filesystem na decisão de caminho/conteúdo.
- `os.utime` fixa mtime/atime idênticos em todos os arquivos.
- O resumo JSON emitido no stdout contém `generator_version`, parâmetros e
  contagens — colar esse resumo no registro da rodada (§6).

## 3. Métricas obrigatórias

Fonte A: bloco `telemetry` do relatório JSON do scanner (schema ADR-0003).
Fonte B: processo via GNU time. Fonte C: relógio monotônico do runner.

| Métrica | Fonte | Definição |
|---|---|---|
| `wall_clock_time` | C | tempo total do scan, monotônico |
| `files_enumerated` | A | entradas enumeradas no Level 0 |
| `files_skipped` | A | descartadas antes de abrir (agrupamento) |
| `files_placeholder` | A | tratadas como NÃO TOCAR |
| `files_partial_hashed` / `partial_hash_count` | A | sobreviveram ao Level 2 (64 KiB início + 64 KiB fim) |
| `files_full_hashed` / `full_hash_count` | A | BLAKE3 completo (Level 3) |
| `bytes_read_partial` | A | bytes lidos no hash parcial |
| `bytes_read_full` | A | bytes lidos no hash completo |
| `bytes_read` | A | soma parcial + full |
| `placeholder_bytes_read` | A | **deve ser 0 sempre** (gate, não métrica) |
| `peak_RSS` | B | `Maximum resident set size (kbytes)` de `/usr/bin/time -v` |
| `CPU` | B | `%CPU` de `/usr/bin/time -v` (user+sys/wall) |

Nota de ambiente: este host de desenvolvimento não tem `/usr/bin/time`
instalado (GNU time). Instalar com `sudo apt-get install time`; enquanto isso
não for possível, capturar `peak_RSS` pelo wrapper stdlib:

```bash
python3 - <<'EOF'
import resource, subprocess, sys, time
t0 = time.perf_counter()
subprocess.run(sys.argv[1:], check=True)
dt = time.perf_counter() - t0
rss_kb = resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss
print(f"wall_clock_s={dt:.3f} peak_rss_kb={rss_kb}")
EOF
```

(`ru_maxrss` em KiB no Linux; equivale ao campo do GNU time.)

## 4. Procedimento de rodada (antes/depois de mudanças)

1. **Congelar o dataset**: mesma versão do gerador, mesmos parâmetros, mesmo
   seed. Registrar o resumo JSON da geração. Recomendado: seed 42 para fumaça,
   seed 20260822 para rodada completa.
2. **Estado da máquina**: máquina na tomada, sem outra carga relevante
   (`uptime` load < 0.5), mesma temperatura/faixa não aplicável em VM — anotar
   host, commit e conditions no registro.
3. **Executar N = 5 scans** do binário/CLI na versão ANTES da mudança:
   ```bash
   /usr/bin/time -v conflictdoctor scan /tmp/cd-dataset --json > run_before_$.json
   ```
   Descartar a primeira execução (aquecimento de cache de página) e reportar a
   **mediana** das 5 seguintes. Para medição cold-cache (opcional, requer
   root): `sync && echo 3 | sudo tee /proc/sys/vm/drop_caches` antes de cada
   execução e registrar que a rodada é cold.
4. Aplicar a mudança (commit separado), repetir o passo 3 como AFTER.
5. Preencher a tabela de comparação (§6) com medianas e deltas %.
6. Arquivar `run_before_*.json` / `run_after_*.json` junto do registro.

O benchmark compara sempre duas versões sobre o MESMO dataset; nunca compara
versões sobre datasets diferentes.

## 5. Meta de 1.000.000 de arquivos (SPEC §23–24) e particionamento

Comando alvo (NÃO executar rotineiramente; ~77 GB, ~15 min de geração):

```bash
python3 scripts/bench/generate_dataset.py \
    --root /tmp/cd-1m --files 1000000 --dup-groups 50000 \
    --conflict-names 50000 --placeholders 60000 --depth 8 --seed 20260822
```

Orçamento estimado a partir da fumaça (média ~77 KiB/arquivo):
espaço ~77 GB, inodes ~1,06 M (host tem 26 M inodes, 172 GB livres — cabe, mas
reservar o disco para isso). Geração estimada entre 10 e 20 min (a fumaça de
530 arquivos levou menos de 1 s).

Particionamento da geração e do scan:

1. **Geração em fatias seedadas**: para não manter o plano inteiro em memória
   nem um diretório gigante só, gerar K fatias independentes e compor a raiz:
   ```bash
   for i in $(seq 0 9); do
     python3 scripts/bench/generate_dataset.py \
       --root /tmp/cd-1m/shard_$i --files 100000 --dup-groups 5000 \
       --conflict-names 5000 --placeholders 6000 --depth 6 --seed $((20260822 + i))
   done
   ```
   Cada fatia é internamente determinística; a composição é apenas união de
   subárvores. O scanner roda sobre `/tmp/cd-1m` (raiz comum).
2. **Scan particionado para diagnóstico**: se for preciso isolar custos, escanear
   shard a shard e somar as telemetrias; a soma deve bater com o scan da raiz
   dentro do overhead de enumeração da raiz (registrar a diferença).
3. **Memória do runner**: `peak_RSS` na meta 1M valida o requisito de o scanner
   não carregar o inventário inteiro sem necessidade; se estourar, o design do
   Level 0 precisa de streaming, não o benchmark de tolerância maior.

GATE 4 (SPEC §45) só fecha com essa rodada executada e registrada.

## 6. Critério de regressão

Tabela-modelo por rodada (mediana de 5, deltas vs. BEFORE):

```text
dataset:        generator_version=1 seed=... params=...
before_commit:  <sha>
after_commit:   <sha>
wall_clock_s:   ... -> ...   (Δ%)
files_enumerated: ... -> ... (Δ%)
files_read:     ... -> ...   (Δ%)
bytes_read:     ... -> ...   (Δ%)
bytes_read_partial / full: ... -> ...
full_hash_count: ... -> ...  (Δ%)
peak_RSS_kb:    ... -> ...   (Δ%)
CPU%:           ... -> ...   (Δ%)
placeholder_bytes_read: 0 -> 0   (gate)
```

Regras:

1. **Gate absoluto**: `placeholder_bytes_read != 0` reprova a rodada
   independente de qualquer ganho de performance.
2. **Regressão**: qualquer uma de `wall_clock_time`, `bytes_read`,
   `files_full_hashed`, `peak_RSS` piorando **mais de 3%** na mediana reprova a
   mudança, exceto melhoria ≥ igual magnitude em outra métrica da função do
   §24, justificada no registro.
3. **Melhoria válida**: reduzir `bytes_read` ou `full_hash_count` sem piorar
   tempo além do item 2 é vitória prioritária, na ordem da SPEC §51
   (correctness > safety > determinism > preservation > performance).
4. Mudanças no formato do relatório exigem novo `report_schema_version`
   (ADR-0003) e re-baseline: primeira rodada após o bump vale só como BEFORE.

## 7. Limitações conhecidas

- ext4 não expõe atributos de placeholder; simulação via stub + sidecar
  (§2.1). A validação definitiva de placeholders ocorre em NTFS/Windows com
  atributos reais.
- Ambiente virtualizado (4 vCPU): números absolutos servem para comparação
  relativa antes/depois no mesmo host; não publicar throughput absoluto como
  garantia de produto.
- `/usr/bin/time` ausente neste host até instalação do pacote GNU time;
  usar o wrapper do §3 nesse período.
